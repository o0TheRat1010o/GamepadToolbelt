using System;
using System.Linq;
using System.Reflection;

namespace GamepadToolbelt.Core;

/// <summary>
/// Checks, before anything is patched, that a Harmony patch method can bind to its target: every parameter that is not
/// a Harmony special (__instance, __result, __state, ...) must exist on the target by name with a compatible type. This
/// catches renamed or retyped game parameters after a game update, which Harmony itself only reports at call time.
/// </summary>
public static class PatchBinding
{
    /// <summary>Null when the patch binds, otherwise the reason it does not.</summary>
    /// <remarks>
    /// A patch may take an argument by reference although the target does not - that is how a Harmony prefix changes an
    /// argument - and then needs exactly the target's type. By value, any type the argument is assignable to works.
    /// </remarks>
    public static string Validate(MethodBase _target, MethodInfo _patch)
    {
        ParameterInfo[] targetParameters = _target.GetParameters();
        foreach (ParameterInfo parameter in _patch.GetParameters())
        {
            if (parameter.Name.StartsWith("__", StringComparison.Ordinal))
            {
                continue;
            }
            ParameterInfo match = targetParameters.FirstOrDefault(p => p.Name == parameter.Name);
            if (match == null)
            {
                return $"parameter '{parameter.Name}' of {_patch.DeclaringType?.Name}.{_patch.Name} does not exist on the target";
            }
            Type expected = match.ParameterType.IsByRef ? match.ParameterType.GetElementType() : match.ParameterType;
            bool byRef = parameter.ParameterType.IsByRef;
            Type actual = byRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType;
            if (expected != actual && (byRef || !(actual != null && actual.IsAssignableFrom(expected))))
            {
                return $"parameter '{parameter.Name}' has type {expected?.Name} on the target, the patch expects {(byRef ? "ref " : string.Empty)}{actual?.Name}";
            }
        }
        return null;
    }
}
