using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace FamilyMemories.Exports;

/// <summary>Renders the shared export templates (/Templates/Export) to strings, outside of a web request.</summary>
public sealed class RazorRenderer(IRazorViewEngine engine, ITempDataProvider tempData, IServiceProvider services)
{
    public async Task<string> RenderAsync<TModel>(string viewPath, TModel model)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var result = engine.GetView(executingFilePath: null, viewPath, isMainPage: true);
        if (!result.Success) throw new InvalidOperationException($"Export template {viewPath} not found");

        await using var writer = new StringWriter();
        var viewData = new ViewDataDictionary<TModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        var context = new ViewContext(actionContext, result.View, viewData, new TempDataDictionary(http, tempData), writer, new HtmlHelperOptions());
        await result.View.RenderAsync(context);
        return writer.ToString();
    }
}
