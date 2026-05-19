using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

namespace Daggeragent.Tools;

/// <summary>
/// Per-job plan state. One plan per job; <see cref="MakePlan"/> overwrites any prior plan.
/// Small models benefit from being forced to commit to an explicit plan before reaching for
/// tools — it stops the "call grep, call read_file, call list_files, repeat" loop they fall
/// into when given a vague task.
/// </summary>
public sealed class PlanStore
{
    private readonly ConcurrentDictionary<string, JobPlan> _plans = new();

    public JobPlan GetOrCreateEmpty(string jobId) => _plans.GetOrAdd(jobId, _ => new JobPlan());

    public JobPlan? Get(string jobId) => _plans.TryGetValue(jobId, out var p) ? p : null;

    public void Set(string jobId, JobPlan plan) => _plans[jobId] = plan;

    public bool HasPlan(string jobId) => _plans.TryGetValue(jobId, out var p) && p.Steps.Count > 0;

    public void Clear(string jobId) => _plans.TryRemove(jobId, out _);
}

public sealed class JobPlan
{
    public List<PlanStep> Steps { get; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PlanStep
{
    public required string Description { get; set; }
    public string Status { get; set; } = "pending"; // pending | in_progress | done | blocked
    public string? Note { get; set; }
}

public sealed class PlanningTools
{
    private readonly PlanStore _store;

    public PlanningTools(PlanStore store)
    {
        _store = store;
    }

    public IEnumerable<AITool> Build(string? jobId)
    {
        var safeJobId = jobId ?? "";

        string MakePlan(
            [Description("Ordered list of short step descriptions. The first step becomes 'in_progress', the rest stay 'pending'.")] string[] steps)
        {
            if (steps is null || steps.Length == 0) return "Error: plan must contain at least one step.";
            var plan = new JobPlan();
            for (var i = 0; i < steps.Length; i++)
            {
                plan.Steps.Add(new PlanStep
                {
                    Description = steps[i],
                    Status = i == 0 ? "in_progress" : "pending",
                });
            }
            _store.Set(safeJobId, plan);
            return RenderPlan(plan);
        }

        string UpdatePlan(
            [Description("Zero-based index of the step to update.")] int step_index,
            [Description("New status. One of: pending | in_progress | done | blocked.")] string status,
            [Description("Optional short note explaining the change.")] string? note = null)
        {
            var plan = _store.Get(safeJobId);
            if (plan is null || plan.Steps.Count == 0) return "Error: no plan exists. Call make_plan first.";
            if (step_index < 0 || step_index >= plan.Steps.Count)
                return $"Error: step_index {step_index} is out of range. Plan has {plan.Steps.Count} steps (0..{plan.Steps.Count - 1}).";

            var normalised = (status ?? "").Trim().ToLowerInvariant();
            if (normalised is not ("pending" or "in_progress" or "done" or "blocked"))
                return $"Error: invalid status '{status}'. Use pending | in_progress | done | blocked.";

            plan.Steps[step_index].Status = normalised;
            if (!string.IsNullOrWhiteSpace(note)) plan.Steps[step_index].Note = note;
            plan.UpdatedAt = DateTimeOffset.UtcNow;
            return RenderPlan(plan);
        }

        yield return AIFunctionFactory.Create(MakePlan, name: "make_plan", description:
            "Commit to a numbered list of steps before doing the work. Overwrites any prior plan for this job. " +
            "Call this early — before reaching for filesystem/shell/web tools — so you stay on-track for multi-step tasks. " +
            "Returns the rendered plan.");

        yield return AIFunctionFactory.Create(UpdatePlan, name: "update_plan", description:
            "Update the status of a single step. Use after finishing a step (done), starting one (in_progress), " +
            "or hitting a blocker. Returns the rendered plan.");
    }

    public static string RenderPlan(JobPlan plan)
    {
        if (plan.Steps.Count == 0) return "(no plan)";
        var sb = new StringBuilder();
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var s = plan.Steps[i];
            var marker = s.Status switch
            {
                "done" => "[x]",
                "in_progress" => "[~]",
                "blocked" => "[!]",
                _ => "[ ]",
            };
            sb.Append(i).Append(". ").Append(marker).Append(' ').Append(s.Description);
            if (!string.IsNullOrWhiteSpace(s.Note)) sb.Append("  — ").Append(s.Note);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
