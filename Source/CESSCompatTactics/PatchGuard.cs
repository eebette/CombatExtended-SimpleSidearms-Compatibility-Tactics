using System;
using HarmonyLib;
using Verse;

namespace CESSCompatTactics
{
    /// <summary>
    /// Failure doctrine for every patch class in this assembly, ported from the core
    /// compat patch (see its PatchGuard.cs for the full rationale), in three layers:
    ///
    /// 1. Attribute pins — every [HarmonyPatch] names its target's full parameter list,
    ///    so an upstream overload cannot make the attribute ambiguous.
    ///
    /// 2. Prepare guards — each class re-resolves its pinned target and skips itself
    ///    with a named, player-readable consequence when the member is gone. One missing
    ///    member costs exactly its own feature.
    ///
    /// 3. Outer/inner method splits — patch bodies reference CE and SS members far
    ///    beyond the patched method, and the JIT resolves those when the body first
    ///    compiles, where no in-method try/catch can see a failure. Each patch entry is
    ///    a thin outer method calling the real body in a NoInlining inner inside
    ///    try/catch: upstream drift surfaces as one named error and the original keeps
    ///    running.
    /// </summary>
    internal static class PatchGuard
    {
        internal const string LogPrefix = "[CE+SS Tactics] ";

        internal static bool Require(Type type, string method, Type[] args, string consequence)
        {
            if (AccessTools.Method(type, method, args) != null)
            {
                return true;
            }
            Log.Error($"{LogPrefix}{type.Name}.{method} not found — {consequence} "
                      + "The mod that declares it probably moved it.");
            return false;
        }
    }
}
