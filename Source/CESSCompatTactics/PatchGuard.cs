using System;
using HarmonyLib;
using Verse;

namespace CESSCompatTactics
{
    /// <summary>
    /// Failure doctrine for every patch class here - so a broken assumption turns a feature off,
    /// never crashes:
    ///
    /// 1. Attribute pins - an exact target signature; a moved/renamed target won't bind.
    /// 2. Prepare guards (Require/RequireType) - confirm the target + depended-on types exist,
    ///    else log the gameplay consequence and skip the class.
    /// 3. Thin-outer/NoInlining-inner split - the outer try/catch keeps a throw out of the game
    ///    (Log.ErrorOnce) and falls back to upstream behavior.
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
            Log.Error($"{LogPrefix}{type.Name}.{method} not found - {consequence} "
                      + "The mod that declares it probably moved it.");
            return false;
        }
    }
}
