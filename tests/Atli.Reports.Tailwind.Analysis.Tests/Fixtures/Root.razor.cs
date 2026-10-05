namespace Fixture;

public partial class Root
{
  private static Type SelectedType => DateTime.Now.Ticks > 0 ? typeof(Leaf) : typeof(StaticChild);
  private const string EscapedClass = "bg-\u0072ed-500";
  private const string ArbitraryClass = "before:content-['hello_world']";
  private const string PrivateSecret = "private-value-must-not-enter-manifest";
  private static readonly Dictionary<string, string> ClassChoices = new()
  {
    ["ok"] = "underline",
    ["bad"] = "line-through",
  };
  private static readonly Dictionary<string, object> SplatAttributes = new()
  {
    ["class"] = "rounded-lg",
    ["data-private"] = PrivateSecret,
  };

  private static string GetStyle()
  {
    Console.WriteLine(PrivateSecret);
    return "align-middle";
  }
}
