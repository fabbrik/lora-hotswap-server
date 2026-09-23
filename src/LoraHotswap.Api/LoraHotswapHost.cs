using LoraHotswap.Core;
using LoraHotswap.Infrastructure;
using OpenTelemetry.Trace;

namespace LoraHotswap.Api;

/// <summary>
/// The result of composing the host: an application ready to run, or the exit code of a
/// startup that failed before any listener was bound.
/// </summary>
/// <param name="App">The composed application, or <see langword="null"/> when startup failed.</param>
/// <param name="ExitCode"><c>0</c> when <paramref name="App"/> is ready; <c>1</c> otherwise.</param>
public sealed record HostStartup(WebApplication? App, int ExitCode);

/// <summary>
/// The composition root: start-up admission, then the serving surface over the admitted
/// registry. No listener binds until admission and strategy initialization succeed (the
/// NFR1 fail-before-listen guarantee). Any startup failure leaves as a reason line on
/// stderr and exit code 1, never a bare stack trace.
///
/// Extracted from <c>Program.cs</c> so the composition itself is testable: a test passes
/// <c>configure</c> to supply configuration, a test server, or extra trace processors.
/// </summary>
public static class LoraHotswapHost
{
    /// <summary>The bound on the startup span flush, so an unreachable collector cannot stall startup.</summary>
    public const int FlushTimeoutMilliseconds = 5000;

    /// <summary>Compose and run the host; returns the process exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        HostStartup startup = await BuildAsync(args, Console.Error);
        if (startup.App is not { } app)
        {
            return startup.ExitCode;
        }

        await app.RunAsync();
        return 0;
    }

    /// <summary>
    /// Compose the host without starting it. On failure the reason is written to
    /// <paramref name="stderr"/>, the partially built application is disposed, and
    /// <see cref="HostStartup.App"/> is <see langword="null"/>.
    /// </summary>
    public static async Task<HostStartup> BuildAsync(
        string[] args, TextWriter stderr, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(stderr);

        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        // The admitted registry is published as a singleton. It is resolved lazily -- only
        // by serving code, which exists strictly after admission has assigned it.
        AdapterRegistry? admittedRegistry = null;

        // The same treatment for the runtime, so "which runtime is serving" is answerable
        // from the container rather than only from the composition root's own local. Read
        // before registering: with a registration of our own in place, "did the caller
        // register one?" could no longer be asked of the container.
        IInferenceRuntime? selectedRuntime = null;
        bool runtimeInjected = builder.Services.Any(d => d.ServiceType == typeof(IInferenceRuntime));
        WebApplication app;
        try
        {
            builder.Services.AddProblemDetails();
            builder.Services.AddLoraHotswapTracing(builder.Configuration);
            builder.Services.AddSingleton(_ =>
                admittedRegistry
                ?? throw new InvalidOperationException("The adapter registry was accessed before admission completed."));
            if (!runtimeInjected)
            {
                builder.Services.AddSingleton(_ =>
                    selectedRuntime
                    ?? throw new InvalidOperationException("The inference runtime was accessed before composition completed."));
            }

            app = builder.Build();

            // GOTCHA: the OpenTelemetry hosted service only builds the TracerProvider when
            // the host starts, which is after admission. Resolve it now, or every admission
            // span is started with no listener and silently dropped.
            app.Services.GetRequiredService<TracerProvider>();
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"startup failed: {ex.Message}");
            return new HostStartup(null, 1);
        }

        HostStartup? failed = await ComposeAsync(
            app, builder, stderr, runtimeInjected,
            registry => admittedRegistry = registry,
            runtime => selectedRuntime = runtime);
        if (failed is not null)
        {
            await app.DisposeAsync();
            return failed;
        }

        return new HostStartup(app, 0);
    }

    private static async Task<HostStartup?> ComposeAsync(
        WebApplication app,
        WebApplicationBuilder builder,
        TextWriter stderr,
        bool runtimeInjected,
        Action<AdapterRegistry> publish,
        Action<IInferenceRuntime> publishRuntime)
    {
        // The runtime seam is read first: selecting a runtime whose configuration is invalid
        // is a startup failure on its own terms, before admission does any work.
        InferenceOptions inferenceOptions = new();
        try
        {
            builder.Configuration.GetSection(InferenceOptions.SectionName).Bind(inferenceOptions);
            inferenceOptions.Validate();
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"startup failed: {ex.Message}");
            return new HostStartup(null, 1);
        }

        // Config binding, validation and admission run after Build() and before Run(): no
        // socket is bound when startup fails.
        AdmissionOptions admissionOptions = new();
        ServingOptions servingOptions = new();
        AdmissionStartupOutcome outcome;
        try
        {
            builder.Configuration.GetSection(AdmissionOptions.SectionName).Bind(admissionOptions);
            admissionOptions.Validate();
            builder.Configuration.GetSection(ServingOptions.SectionName).Bind(servingOptions);
            servingOptions.Validate();

            outcome = await AdmissionStartup.RunAsync(
                admissionOptions.ResolveAdapterDirectories(builder.Environment.ContentRootPath),
                admissionOptions.BaseModelName,
                admissionOptions.TenantMap(),
                new FileSystemAdapterArtifactsReader(),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"admission failed: {ex.Message}");
            return new HostStartup(null, 1);
        }
        finally
        {
            app.Services.GetRequiredService<TracerProvider>().ForceFlush(FlushTimeoutMilliseconds);
        }

        if (!outcome.Succeeded)
        {
            foreach (var problem in outcome.Problems)
            {
                await stderr.WriteLineAsync(problem.ToStderrLine());
            }

            return new HostStartup(null, 1);
        }

        AdapterRegistry registry = outcome.Registry!;
        publish(registry);

        // The runtime is chosen by configuration and nothing else changes (AD-8). The fake is
        // the default; a registered runtime still takes precedence (tests inject faults that
        // way). Only a runtime this method constructs is a runtime this method must dispose.
        IInferenceRuntime runtime;
        IDisposable? owned = null;
        if (runtimeInjected)
        {
            // A registered runtime that quietly outranked an explicit LLamaSharp selection
            // would serve with no model and no asset check ever running -- the configuration
            // would say one thing and the process do another.
            if (inferenceOptions.UsesLlamaSharp)
            {
                await stderr.WriteLineAsync(
                    $"startup failed: {InferenceOptions.SectionName}: Runtime is '{InferenceOptions.LlamaSharpRuntimeName}' " +
                    "but an IInferenceRuntime is already registered; the registered one would serve and no model would be loaded.");
                return new HostStartup(null, 1);
            }

            runtime = app.Services.GetRequiredService<IInferenceRuntime>();
        }
        else if (inferenceOptions.UsesLlamaSharp)
        {
            try
            {
                // Loading the base model is where a missing GGUF is discovered. It happens
                // here, before any listener binds, so the asset check cannot be reached by a
                // request that would otherwise be answered from the base model.
                LlamaSharpInferenceRuntime real = LlamaSharpInferenceRuntime.Load(
                    inferenceOptions.CreateSettings(app.Environment.ContentRootPath, outcome.AdapterDirectories));
                runtime = real;
                owned = real;
            }
            catch (Exception ex)
            {
                await stderr.WriteLineAsync($"startup failed: {ex.Message}");
                return new HostStartup(null, 1);
            }

            // A runtime this method owns outlives composition but not the host.
            app.Services.GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStopped.Register(owned.Dispose);
        }
        else
        {
            runtime = new DeterministicInferenceRuntime();
        }

        publishRuntime(runtime);

        // Named once, at Information, so "which runtime served this?" has an answer in the
        // log as well as in the container -- the response shape deliberately does not carry it.
        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(LoraHotswapHost).FullName!)
            .LogInformation(
                "Serving through the {InferenceRuntime} inference runtime ({InferenceRuntimeType}).",
                inferenceOptions.Runtime, runtime.GetType().Name);

        IContextBindingStrategy strategy = servingOptions.CreateStrategy(runtime);
        try
        {
            // PerTenantResident applies every tenant's adapter here; a failure is a startup
            // failure, never a downgrade to serving without the adapter.
            await strategy.InitializeAsync(registry, CancellationToken.None);
        }
        catch (RequestProblemException ex)
        {
            owned?.Dispose();
            await stderr.WriteLineAsync($"{ex.Problem.Type} | {ex.Problem.Title}: {ex.Problem.Detail}");
            return new HostStartup(null, 1);
        }
        catch (Exception ex)
        {
            owned?.Dispose();
            await stderr.WriteLineAsync($"startup failed: {ex.GetType().Name} while initializing the {strategy.Name} strategy.");
            return new HostStartup(null, 1);
        }

        var serving = new TenantServingService(registry, runtime, strategy, admissionOptions.BaseModelName);
        var verifier = new SafeTensorsAdapterVerifier(outcome.AdapterDirectories);
        app.MapServingEndpoints(serving, registry, verifier);
        return null;
    }
}
