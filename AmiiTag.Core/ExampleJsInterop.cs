using System.Runtime.InteropServices.JavaScript;

namespace AmiiTag.RazorShared
{
	// This class provides an example of how JavaScript functionality can be wrapped
	// in a .NET class for easy consumption. The associated JavaScript module is
	// loaded on demand when first needed.
	//
	// This class can be registered as scoped DI service and then injected into Blazor
	// components for use.

	public partial class ExampleJsInterop
	{
		[JSImport("showPrompt", "./_content/AmiiTag.RazorShared/exampleJsInterop.js")]
		public static partial string Prompt(string message);
	}
}