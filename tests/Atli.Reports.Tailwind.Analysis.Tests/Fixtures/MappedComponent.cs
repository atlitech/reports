using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Fixture;

public sealed class MappedComponent : ComponentBase
{
  protected override void BuildRenderTree(RenderTreeBuilder builder)
  {
#line 72 "virtual/Original.template"
    builder.OpenElement(0, "span");
    builder.AddAttribute(1, "class", "sr-only");
    builder.CloseElement();
#line default
  }
}
