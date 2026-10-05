using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Fixture;

public abstract class StyledBase : ComponentBase
{
  protected string BaseStyles => "font-semibold leading-tight";

  protected override void BuildRenderTree(RenderTreeBuilder builder)
  {
    builder.OpenComponent<InheritedLeaf>(0);
    builder.CloseComponent();
  }
}

public sealed class InheritedLeaf : ComponentBase
{
  protected override void BuildRenderTree(RenderTreeBuilder builder)
  {
    builder.OpenElement(0, "strong");
    builder.AddAttribute(1, "class", "whitespace-nowrap");
    builder.AddMultipleAttributes(
      2,
      new[]
      {
        new KeyValuePair<string, object>("class", "italic"),
        new KeyValuePair<string, object>("data-private", "manual-private-must-not-enter-manifest"),
      }
    );
    builder.CloseElement();
  }
}
