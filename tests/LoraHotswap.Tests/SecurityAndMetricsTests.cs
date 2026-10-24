using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LoraHotswap.Api;
using LoraHotswap.Core;
using LoraHotswap.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LoraHotswap.Tests;

/// <summary>Per-tenant API keys, the admin key, <c>/healthz</c> and <c>/metrics</c>, over the real route handlers.</summary>
public sealed class SecurityAndMetricsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AliceKey = "alice-secret-key";
    private const string BobKey = "bob-secret-key";
    private const string AdminKey = "admin-secret-key";

    private static AuthOptions Auth() => new()
    {
        TenantKeySha256 = new(StringComparer.Ordinal)
        {
            ["alice"] = EndpointSecurity.Digest(AliceKey),
            ["bob"] = EndpointSecurity.Digest(BobKey),
            ["carol"] = EndpointSecurity.Digest("carol-secret-key"),
        },
        AdminKeySha256 = EndpointSecurity.Digest(AdminKey),
    };

    private static async Task<(WebApplication App, HttpClient Client, ServingMetrics Metrics)> StartAsync(AuthOptions auth)
    {
        var world = await TenantServingTests.World.CreateAsync(PerRequestApplyStrategy.StrategyName);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        WebApplication app = builder.Build();
        var metrics = new ServingMetrics(world.Registry.Tenants.Keys.Select(t => t.Value));
        app.MapServingEndpoints(world.Service, world.Registry, new SafeTensorsAdapterVerifier(new Dictionary<AdapterId, string>()),
            new EndpointSecurity(auth), metrics);
        await app.StartAsync();
        return (app, app.GetTestClient(), metrics);
    }

    private static HttpRequestMessage Generate(string tenant, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/generate") { Content = JsonContent.Create(new { tenant, prompt = "hi" }) };
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return request;
    }

    [Fact]
    public async Task With_no_keys_configured_the_server_stays_open()
    {
        var (app, client, _) = await StartAsync(new AuthOptions());
        await using var _a = app;

        using HttpResponseMessage response = await client.SendAsync(Generate("alice", key: null), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_tenant_key_serves_that_tenant()
    {
        var (app, client, _) = await StartAsync(Auth());
        await using var _a = app;

        using HttpResponseMessage response = await client.SendAsync(Generate("alice", AliceKey), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    [InlineData(BobKey)]   // another tenant's key must not serve alice
    [InlineData(AdminKey)] // the admin key is an operator, not a tenant
    public async Task A_missing_or_wrong_key_is_401(string? key)
    {
        var (app, client, _) = await StartAsync(Auth());
        await using var _a = app;

        using HttpResponseMessage response = await client.SendAsync(Generate("alice", key), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(ProblemTypes.Unauthorized, body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task An_unknown_tenant_cannot_be_told_from_a_wrong_key()
    {
        var (app, client, _) = await StartAsync(Auth());
        await using var _a = app;

        using HttpResponseMessage unknown = await client.SendAsync(Generate("mallory", AliceKey), Ct);
        using HttpResponseMessage wrong = await client.SendAsync(Generate("alice", "wrong-key"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(
            (await unknown.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString(),
            (await wrong.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Verification_and_metrics_need_the_admin_key_but_healthz_stays_open()
    {
        var (app, client, _) = await StartAsync(Auth());
        await using var _a = app;

        using HttpResponseMessage health = await client.GetAsync("/healthz", Ct);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using HttpResponseMessage anonymousMetrics = await client.GetAsync("/metrics", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousMetrics.StatusCode);

        using var tenantMetrics = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        tenantMetrics.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AliceKey);
        using HttpResponseMessage tenantAttempt = await client.SendAsync(tenantMetrics, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, tenantAttempt.StatusCode);

        using var adminMetrics = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        adminMetrics.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);
        using HttpResponseMessage admin = await client.SendAsync(adminMetrics, Ct);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);

        string id = "sha256:" + new string('a', 64);
        using HttpResponseMessage anonymousVerify = await client.PostAsync($"/v1/adapters/{id}/verify", content: null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousVerify.StatusCode);
    }

    [Fact]
    public async Task Metrics_count_served_and_failed_requests_per_tenant()
    {
        var (app, client, _) = await StartAsync(new AuthOptions());
        await using var _a = app;

        (await client.SendAsync(Generate("alice", null), Ct)).Dispose();
        (await client.SendAsync(Generate("alice", null), Ct)).Dispose();
        (await client.SendAsync(Generate("mallory", null), Ct)).Dispose();

        string text = await client.GetStringAsync("/metrics", Ct);

        Assert.Contains("lorahotswap_requests_total{tenant=\"alice\",outcome=\"ok\"} 2", text, StringComparison.Ordinal);
        Assert.Contains("lorahotswap_requests_total{tenant=\"_unknown\",outcome=\"unknown-tenant\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("lorahotswap_generation_seconds_count{tenant=\"alice\"} 2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("mallory", text, StringComparison.Ordinal); // caller-chosen strings never become labels
    }

    [Fact]
    public async Task Healthz_reports_the_tenant_count()
    {
        var (app, client, _) = await StartAsync(new AuthOptions());
        await using var _a = app;

        JsonElement body = await client.GetFromJsonAsync<JsonElement>("/healthz", Ct);

        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal(3, body.GetProperty("tenants").GetInt32());
    }

    [Fact]
    public void Enabling_auth_without_a_key_for_every_tenant_fails_startup()
    {
        AuthOptions options = Auth();
        options.TenantKeySha256.Remove("carol");

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(["alice", "bob", "carol"]));

        Assert.Contains("carol", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabling_auth_without_an_admin_key_fails_startup()
    {
        AuthOptions options = Auth();
        options.AdminKeySha256 = "";

        Assert.Throws<InvalidOperationException>(() => options.Validate(["alice", "bob", "carol"]));
    }

    [Fact]
    public void A_key_that_is_not_a_sha256_digest_fails_startup()
    {
        AuthOptions options = Auth();
        options.TenantKeySha256["alice"] = "my-plaintext-password";

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(["alice", "bob", "carol"]));

        Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_for_a_tenant_that_does_not_exist_fails_startup()
    {
        AuthOptions options = Auth();
        options.TenantKeySha256["dave"] = EndpointSecurity.Digest("x");

        Assert.Throws<InvalidOperationException>(() => options.Validate(["alice", "bob", "carol"]));
    }
}
