using LoraHotswap.Api;
using LoraHotswap.Core;
using LoraHotswap.Infrastructure;

// Host only. The HTTP surface belongs to Story 2.1. This story's only host
// behavior is start-up admission and the resulting process exit code.
var builder = WebApplication.CreateBuilder(args);

// The admitted registry is published as a singleton for later stories. It is
// resolved lazily -- the closure below is only invoked once serving code exists,
// which is strictly after admission has assigned the registry (or the process has
// already exited). Nothing binds a listener before that point.
AdapterRegistry? admittedRegistry = null;
builder.Services.AddSingleton(_ =>
    admittedRegistry
    ?? throw new InvalidOperationException("The adapter registry was accessed before admission completed."));

var app = builder.Build();

// Admission runs after Build() and before Run(): no socket is bound when the
// process exits non-zero on failure (the NFR1 fail-before-listen guarantee). The
// whole config-binding + validation + admission run is wrapped so that ANY failure
// -- a validation error, malformed manifest/config JSON, an unreadable directory --
// leaves as a reason line on stderr and exit code 1, never a bare stack trace.
AdmissionStartupOutcome outcome;
try
{
    var options = new AdmissionOptions();
    builder.Configuration.GetSection(AdmissionOptions.SectionName).Bind(options);
    options.Validate();

    var reader = new FileSystemAdapterArtifactsReader();
    outcome = await AdmissionStartup.RunAsync(
        options.ResolveAdapterDirectories(builder.Environment.ContentRootPath),
        options.BaseModelName,
        options.TenantMap(),
        reader,
        CancellationToken.None);
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"admission failed: {ex.Message}");
    return 1;
}

if (!outcome.Succeeded)
{
    foreach (var problem in outcome.Problems)
    {
        await Console.Error.WriteLineAsync(problem.ToStderrLine());
    }

    return 1;
}

admittedRegistry = outcome.Registry;
app.Run();
return 0;
