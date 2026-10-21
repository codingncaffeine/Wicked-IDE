namespace Wicked.Core.Extensions;

/// <summary>What init.csx sees as globals: the same API an extension is given, as <c>wicked</c>.</summary>
public sealed class InitGlobals(IWickedApi wicked)
{
#pragma warning disable IDE1006 // the script spells it in lower case, like a variable
    public IWickedApi wicked { get; } = wicked;
#pragma warning restore IDE1006
}
