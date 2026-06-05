using Daggeragent.Agent;
using Daggeragent.Configuration;
using Daggeragent.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Daggeragent.Server;

public static class JobsEndpoints
{
    public static IEndpointRouteBuilder MapJobsApi(this IEndpointRouteBuilder app, string basePath = "/agent")
    {
        var group = app.MapGroup(basePath);

        group.MapGet("/healthz", async (
            IJobStore store,
            Daggeragent.Mcp.McpClientHost mcpHost,
            CancellationToken ct) =>
        {
            var checks = new Dictionary<string, object>();

            var sqliteOk = await store.HealthCheckAsync(ct).ConfigureAwait(false);
            checks["sqlite"] = sqliteOk ? new { status = "ok" } : (object)new { status = "unhealthy" };

            var mcpTimeout = TimeSpan.FromSeconds(2);
            var anyMcpUnhealthy = false;
            foreach (var name in mcpHost.Clients.Keys)
            {
                var pong = await mcpHost.PingAsync(name, mcpTimeout, ct).ConfigureAwait(false);
                checks[$"mcp.{name}"] = pong
                    ? (object)new { status = "ok" }
                    : new { status = "unhealthy" };
                if (!pong) anyMcpUnhealthy = true;
            }

            string overall;
            int statusCode;
            if (!sqliteOk) { overall = "unhealthy"; statusCode = StatusCodes.Status503ServiceUnavailable; }
            else if (anyMcpUnhealthy) { overall = "degraded"; statusCode = StatusCodes.Status200OK; }
            else { overall = "ok"; statusCode = StatusCodes.Status200OK; }

            return Results.Json(new { status = overall, checks }, statusCode: statusCode);
        });

        group.MapPost("/jobs", async (
            CreateJobRequest req,
            LlmAgent agent,
            IJobStore store,
            IOptions<OpenAIOptions> openAi,
            IOptions<EndpointsOptions> endpoints,
            IOptions<AgentOptions> agentOpts,
            CancellationToken ct) =>
        {
            await store.InitializeAsync(ct);
            var model = JobsStreamEndpoints.ResolveModel(req.Model, req.EndpointId, endpoints.Value, openAi.Value);
            var state = agent.CreateState(model, req.System);
            if (!string.IsNullOrWhiteSpace(req.EndpointId)) state.EndpointId = req.EndpointId;
            var response = await agent.RunTurnAsync(state, req.Prompt, ct);
            return Results.Ok(new CreateJobResponse(state.Id, state.Status.ToString(), state.Model, response.Text ?? ""));
        });

        group.MapGet("/jobs/{id}", async (string id, IJobStore store, CancellationToken ct) =>
        {
            var state = await store.LoadAsync(id, ct);
            if (state is null) return Results.NotFound();
            return Results.Ok(ToView(state));
        });

        group.MapPost("/jobs/{id}/messages", async (
            string id,
            SendMessageRequest req,
            LlmAgent agent,
            IJobStore store,
            CancellationToken ct) =>
        {
            var state = await store.LoadAsync(id, ct);
            if (state is null) return Results.NotFound();
            var response = await agent.RunTurnAsync(state, req.Prompt, ct);
            return Results.Ok(new SendMessageResponse(state.Id, state.Status.ToString(), response.Text ?? ""));
        });

        group.MapPost("/jobs/{id}/resume", async (
            string id,
            LlmAgent agent,
            IJobStore store,
            CancellationToken ct) =>
        {
            var state = await store.LoadAsync(id, ct);
            if (state is null) return Results.NotFound();
            // ResumeAsync injects a recovery-flavoured prompt that nudges the model to pick up
            // from in-progress plan steps rather than starting over. Also clears Interrupted.
            var response = await agent.ResumeAsync(state, ct);
            return Results.Ok(new SendMessageResponse(state.Id, state.Status.ToString(), response.Text ?? ""));
        });

        group.MapDelete("/jobs/{id}", async (string id, IJobStore store, CancellationToken ct) =>
        {
            await store.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        group.MapGet("/jobs", async (IJobStore store, int? limit, CancellationToken ct) =>
        {
            var rows = await store.ListAsync(limit ?? 50, ct);
            return Results.Ok(rows.Select(r => new
            {
                jobId = r.Id,
                parentId = r.ParentId,
                status = r.Status,
                model = r.Model,
                createdAt = r.CreatedAt,
                updatedAt = r.UpdatedAt,
                interrupted = r.Interrupted,
                triggerSourceId = r.TriggerSourceId,
            }));
        });

        return app;
    }

    private static JobView ToView(ConversationState state) => new(
        state.Id,
        state.ParentId,
        state.Status.ToString(),
        state.Model,
        state.SystemPrompt,
        state.TurnsTaken,
        state.ApproxTokenCount,
        state.TotalInputTokens,
        state.TotalOutputTokens,
        state.TotalThinkingTokens,
        state.TotalCostUsd,
        state.CreatedAt,
        state.UpdatedAt,
        state.History.Select(m => new MessageView(m.Role.Value, m.Text ?? "")).ToList(),
        WorkingDirectory: string.IsNullOrEmpty(state.WorkingDirectory) ? null : state.WorkingDirectory,
        EndpointId: state.EndpointId);
}
