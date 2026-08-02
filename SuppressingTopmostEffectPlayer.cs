using System.Reflection;

namespace ClassIslandInjector;

/// <summary>
/// Replaces the player captured by MainWindowLine while the plugin owns ripple
/// rendering. This avoids changing the user's persistent ClassIsland settings.
/// </summary>
public class SuppressingTopmostEffectPlayer : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        return null;
    }
}
