using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Services.Queue;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Architecture-test regression guard for issue #1666: the OctoPrint-compat upload endpoint
/// (<c>POST /api/files/local</c>) enqueued print jobs via
/// <see cref="IJobQueueService.AddJobToQueueAsync"/> with no permission check at all, because
/// <c>[AllowAnonymous]</c> skipped the authorization middleware entirely. This test performs a
/// best-effort static call-graph walk from every controller action in <see cref="WalkableAssemblies"/> and
/// fails if any action that transitively reaches <see cref="IJobQueueService.AddJobToQueueAsync"/>
/// does not itself carry a <c>queue:write</c>-or-stronger <see cref="RequirePermissionAttribute"/>
/// (method- or class-level), so a future endpoint cannot reintroduce the same class of bug.
///
/// Companion to <see cref="AuthorizeRolesGateArchitectureTests"/>, which guards a different
/// (role-name) bypass on the same "every action must carry a real permission gate" principle.
/// </summary>
public sealed class QueueEnqueuePermissionArchitectureTests
{
    /// <summary>
    /// Explicit, minimal allowlist for genuine exceptions where an action transitively reaches
    /// <see cref="IJobQueueService.AddJobToQueueAsync"/> but is intentionally gated by a
    /// different (stronger or orthogonal) permission instead of a literal <c>queue:write</c> or
    /// <c>queue:admin</c> grant. Each entry MUST carry a written reason as an inline comment.
    /// Entries are "Namespace.Type.Method".
    /// </summary>
    private static readonly HashSet<string> Allowlist = new(StringComparer.Ordinal)
    {
        // PrintApprovalsController.ApproveAsync/RejectAsync are farm_admin-only administrative
        // overrides gated by "job_queue:admin" (a distinct resource from "queue" by design — see
        // DatabaseInitializer's resource seed comments). They call PrintApprovalService, which
        // calls AddJobToQueueAsync with a null userId (issue #1666 item 6, tracked separately;
        // acceptable here because the action itself is already farm_admin-gated end to end).
        "Farm.Web.Api.Controllers.PrintApprovalsController.ApproveAsync",
        "Farm.Web.Api.Controllers.PrintApprovalsController.RejectAsync",
    };

    /// <summary>
    /// Assemblies whose methods are eligible to be walked as call-graph nodes. Restricting the
    /// walk to the main API, print-queue module, and infrastructure assemblies keeps the graph
    /// bounded (no wandering into EF Core, BCL collections, etc.) while still covering every real
    /// production call site of <see cref="IJobQueueService.AddJobToQueueAsync"/>. The print-queue
    /// module assembly is included because issue #2040 moved JobQueueController,
    /// SlicePrintBridgeController, and PrintApprovalsController — all real call sites of
    /// AddJobToQueueAsync — out of Farm.Web.Api and into Farm.Modules.PrintQueue; without this
    /// entry the walk (and the controller-enumeration loop below, which also scans
    /// WalkableAssemblies) would silently stop inspecting those controllers instead of failing
    /// loudly.
    /// </summary>
    private static readonly HashSet<Assembly> WalkableAssemblies = new()
    {
        typeof(Farm.Web.Api.Controllers.PrintersController).Assembly, // Farm.Web.Api
        typeof(IJobQueueService).Assembly, // Farm.Infrastructure
        typeof(Farm.Web.Api.Controllers.JobQueueController).Assembly, // Farm.Modules.PrintQueue
    };

    private static readonly Dictionary<short, OpCode> OpCodesByValue = BuildOpCodeMap();

    [Fact]
    public void EveryControllerActionThatEnqueuesAJob_CarriesQueueWriteOrStrongerPermission()
    {
        MethodInfo targetMethod = typeof(IJobQueueService).GetMethod(nameof(IJobQueueService.AddJobToQueueAsync))
            ?? throw new InvalidOperationException(
                $"{nameof(IJobQueueService)}.{nameof(IJobQueueService.AddJobToQueueAsync)} not found — has it been renamed?");

        List<string> offenders = [];
        int actionsReachingTarget = 0;

        // Enumerate controller types across every walkable assembly, not just Farm.Web.Api:
        // issue #2040 moved several controller entry points (JobQueueController,
        // SlicePrintBridgeController, PrintApprovalsController) into Farm.Modules.PrintQueue, so
        // restricting this loop to Farm.Web.Api would silently stop scanning them.
        foreach (Type controllerType in WalkableAssemblies.SelectMany(a => a.GetTypes()).Distinct())
        {
            if (!typeof(ControllerBase).IsAssignableFrom(controllerType) || controllerType.IsAbstract)
            {
                continue;
            }

            foreach (MethodInfo action in controllerType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (action.IsSpecialName || action.GetCustomAttribute<NonActionAttribute>() is not null)
                {
                    continue;
                }

                string displayName = $"{controllerType.FullName}.{action.Name}";

                if (!CallGraphReachesTarget(action, targetMethod))
                {
                    continue;
                }

                actionsReachingTarget++;

                if (Allowlist.Contains(displayName))
                {
                    continue;
                }

                if (!HasQueueWriteOrStrongerPermission(action, controllerType))
                {
                    offenders.Add(displayName);
                }
            }
        }

        // Sanity check: if the call-graph walk finds zero actions reaching the target at all,
        // the walker itself is broken (e.g. a .NET runtime change altered async state-machine
        // shape or IL opcode layout) and this test would pass vacuously. Fail loudly instead.
        actionsReachingTarget.Should().BeGreaterThan(
            0,
            "the static call-graph walk should find at least the known production call sites " +
            "(JobQueueController, OctoPrintCompatController, SlicePrintBridgeController, " +
            "PrintProjectsController, PrintApprovalsController) reaching " +
            "IJobQueueService.AddJobToQueueAsync — finding none means the walker itself is " +
            "broken, not that the codebase is clean");

        offenders.Should().BeEmpty(
            "issue #1666 requires every controller action that transitively calls " +
            "IJobQueueService.AddJobToQueueAsync to carry a queue:write-or-stronger " +
            "[RequirePermission] gate — enqueuing a print job with no permission check is " +
            "exactly the vulnerability this issue fixed. Add [RequirePermission(queue:write)] " +
            "(or queue:admin) to the offending action(s), or add a documented, reasoned entry " +
            $"to {nameof(Allowlist)} if a genuine exception applies. Offenders: " +
            string.Join(", ", offenders));
    }

    private static bool HasQueueWriteOrStrongerPermission(MethodInfo action, Type controllerType)
    {
        IEnumerable<RequirePermissionAttribute> attributes = action
            .GetCustomAttributes<RequirePermissionAttribute>(inherit: true)
            .Concat(controllerType.GetCustomAttributes<RequirePermissionAttribute>(inherit: true));

        return attributes.Any(a =>
            string.Equals(a.Resource, "queue", StringComparison.Ordinal) &&
            (string.Equals(a.Action, "write", StringComparison.Ordinal) ||
             string.Equals(a.Action, "admin", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Breadth-first, best-effort static call-graph walk from <paramref name="entryPoint"/>,
    /// following <c>call</c>/<c>callvirt</c>/<c>newobj</c> targets resolved from IL method
    /// bodies, until <paramref name="target"/> is reached or the reachable graph (restricted to
    /// <see cref="WalkableAssemblies"/>) is exhausted.
    /// </summary>
    private static bool CallGraphReachesTarget(MethodBase entryPoint, MethodInfo target)
    {
        var visited = new HashSet<MethodBase>();
        var queue = new Queue<MethodBase>();
        queue.Enqueue(entryPoint);

        while (queue.Count > 0)
        {
            MethodBase current = queue.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            foreach (MethodBase callee in ExtractCalledMethods(current))
            {
                if (IsTargetMethod(callee, target))
                {
                    return true;
                }

                if (callee.DeclaringType?.Assembly is Assembly asm &&
                    WalkableAssemblies.Contains(asm) &&
                    !visited.Contains(callee))
                {
                    queue.Enqueue(callee);
                }
            }
        }

        return false;
    }

    private static bool IsTargetMethod(MethodBase candidate, MethodInfo target)
    {
        if (candidate == target)
        {
            return true;
        }

        // A callvirt against an interface member normally resolves directly to that interface's
        // MethodInfo (matched by reference-equality above). This name+declaring-type fallback
        // covers generic-instantiation edge cases where token resolution yields a distinct but
        // equivalent MethodInfo instance.
        return candidate.Name == target.Name && candidate.DeclaringType == target.DeclaringType;
    }

    /// <summary>
    /// Extracts every method referenced by a <c>call</c>, <c>callvirt</c>, or <c>newobj</c>
    /// instruction in <paramref name="method"/>'s IL body. For an <c>async</c> method, the
    /// visible method body is just a small stub that starts the compiler-generated state
    /// machine — the real logic lives in that state machine's <c>MoveNext</c> method, so this
    /// resolves to that instead.
    /// </summary>
    private static IEnumerable<MethodBase> ExtractCalledMethods(MethodBase method)
    {
        MethodBase bodyMethod = ResolveActualBodyMethod(method);
        MethodBody? body;
        try
        {
            body = bodyMethod.GetMethodBody();
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            yield break;
        }

        if (body is null)
        {
            yield break;
        }

        byte[]? il = body.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        Type[]? typeArgs = bodyMethod.DeclaringType?.IsGenericType == true
            ? bodyMethod.DeclaringType.GetGenericArguments()
            : null;
        Type[]? methodArgs = bodyMethod is MethodInfo { IsGenericMethod: true } genericMethod
            ? genericMethod.GetGenericArguments()
            : null;

        int position = 0;
        while (position < il.Length)
        {
            byte code = il[position];
            position++;
            OpCode opCode;
            if (code == 0xFE)
            {
                byte code2 = il[position];
                position++;
                if (!OpCodesByValue.TryGetValue((short)(0xFE00 | code2), out opCode))
                {
                    // Unknown two-byte opcode — cannot safely determine operand size, abort
                    // walking this method body (best-effort walk).
                    yield break;
                }
            }
            else if (!OpCodesByValue.TryGetValue(code, out opCode))
            {
                yield break;
            }

            if (opCode.OperandType == OperandType.InlineSwitch)
            {
                if (position + 4 > il.Length)
                {
                    yield break;
                }

                int caseCount = BitConverter.ToInt32(il, position);
                position += 4 + (caseCount * 4);
                continue;
            }

            int operandSize = GetOperandSize(opCode.OperandType);
            bool isMethodToken = opCode.OperandType is OperandType.InlineMethod;
            MethodBase? resolved = null;
            if (isMethodToken && position + 4 <= il.Length)
            {
                int token = BitConverter.ToInt32(il, position);
                try
                {
                    resolved = bodyMethod.Module.ResolveMethod(token, typeArgs, methodArgs);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    // Best-effort: some tokens (e.g. vararg call sites) are not resolvable this
                    // way. Skip rather than fail the whole walk.
                }
            }

            position += operandSize;

            if (resolved is not null)
            {
                yield return resolved;
            }
        }
    }

    private static MethodBase ResolveActualBodyMethod(MethodBase method)
    {
        AsyncStateMachineAttribute? asyncAttribute = method.GetCustomAttribute<AsyncStateMachineAttribute>();
        if (asyncAttribute?.StateMachineType is Type stateMachineType)
        {
            MethodInfo? moveNext = stateMachineType.GetMethod(
                "MoveNext",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (moveNext is not null)
            {
                return moveNext;
            }
        }

        return method;
    }

    private static int GetOperandSize(OperandType operandType) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget => 1,
        OperandType.ShortInlineI => 1,
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget => 4,
        OperandType.InlineField => 4,
        OperandType.InlineI => 4,
        OperandType.InlineMethod => 4,
        OperandType.InlineSig => 4,
        OperandType.InlineString => 4,
        OperandType.InlineTok => 4,
        OperandType.InlineType => 4,
        OperandType.ShortInlineR => 4,
        OperandType.InlineI8 => 8,
        OperandType.InlineR => 8,
        _ => throw new NotSupportedException($"Unsupported IL operand type '{operandType}' encountered while walking a method body."),
    };

    private static Dictionary<short, OpCode> BuildOpCodeMap()
    {
        var map = new Dictionary<short, OpCode>();
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opCode)
            {
                map[opCode.Value] = opCode;
            }
        }

        return map;
    }
}
