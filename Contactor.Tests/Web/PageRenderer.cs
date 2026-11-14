using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// A test renderer has to read the render tree, as bUnit does; the app itself never does.
#pragma warning disable BL0006

namespace Contactor.Tests.Web;

/// <summary>
/// Runs a page live, as a circuit would, with the app's services and one signed-in person: renders it, finds a button
/// by its attributes and presses it through the page's own handler. It counts the page's renders, so a test can tell
/// whether a change redrew it, and disposing it disposes the page.
/// </summary>
internal sealed class PageRenderer : Renderer
{
    private readonly AsyncServiceScope _scope;
    private readonly List<Exception> _errors = [];
    private int _renders;
    private int _rootId;

    private PageRenderer(AsyncServiceScope scope)
        : base(scope.ServiceProvider, NullLoggerFactory.Instance)
    {
        _scope = scope;
    }

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    /// <summary>How many times anything on the page was drawn.</summary>
    public int Renders => Volatile.Read(ref _renders);

    /// <summary>What went wrong inside the page, which would have ended its live connection.</summary>
    public IReadOnlyList<Exception> Errors => _errors;

    /// <summary>Renders <typeparamref name="TPage"/> for <paramref name="email"/> (shown as <paramref name="name"/>).</summary>
    public static async Task<PageRenderer> StartAsync<TPage>(IServiceProvider app, string email, string name, IDictionary<string, object?>? parameters = null)
        where TPage : IComponent
    {
        var scope = app.CreateAsyncScope();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Name, name)], "Test"));
        ((IHostEnvironmentAuthenticationStateProvider)scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>())
            .SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        var renderer = new PageRenderer(scope);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var page = renderer.InstantiateComponent(typeof(TPage));
            renderer._rootId = renderer.AssignRootComponentId(page);
            await renderer.RenderRootComponentAsync(renderer._rootId, ParameterView.FromDictionary(parameters ?? new Dictionary<string, object?>()));
        });
        return renderer;
    }

    /// <summary>Whether the page now has an element whose attributes match.</summary>
    public Task<bool> HasAsync(Func<IReadOnlyDictionary<string, object?>, bool> match)
    {
        return Dispatcher.InvokeAsync(() => Find(_rootId, match) is not null);
    }

    /// <summary>Presses the first button whose attributes match, through its <c>onclick</c>.</summary>
    public async Task ClickAsync(Func<IReadOnlyDictionary<string, object?>, bool> match)
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            var element = Find(_rootId, match) ?? throw new InvalidOperationException("No such element on the page.");
            var handler = element.Handlers.TryGetValue("onclick", out var id) ? id : throw new InvalidOperationException("It has no onclick.");
            await DispatchEventAsync(handler, null, new MouseEventArgs());
        });
    }

    /// <summary>An attribute match: the element's <paramref name="name"/> attribute is <paramref name="value"/>.</summary>
    public static Func<IReadOnlyDictionary<string, object?>, bool> With(string name, string value)
    {
        return attributes => attributes.TryGetValue(name, out var found) && found?.ToString() == value;
    }

    /// <summary>Closes the page, as when the person leaves it: the page is disposed.</summary>
    public void Close()
    {
        Dispose();
    }

    protected override void HandleException(Exception exception)
    {
        lock (_errors)
        {
            _errors.Add(exception);
        }
    }

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
    {
        Interlocked.Increment(ref _renders);
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _scope.Dispose();
        }
    }

    // Walks the component's frames and its child components' for an element whose attributes match.
    private Element? Find(int componentId, Func<IReadOnlyDictionary<string, object?>, bool> match)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        for (var i = 0; i < frames.Count; i++)
        {
            ref var frame = ref frames.Array[i];
            if (frame.FrameType == RenderTreeFrameType.Element)
            {
                var attributes = new Dictionary<string, object?>();
                var handlers = new Dictionary<string, ulong>();
                for (var a = i + 1; a < frames.Count && frames.Array[a].FrameType == RenderTreeFrameType.Attribute; a++)
                {
                    ref var attribute = ref frames.Array[a];
                    attributes[attribute.AttributeName] = attribute.AttributeValue;
                    if (attribute.AttributeEventHandlerId != 0)
                    {
                        handlers[attribute.AttributeName] = attribute.AttributeEventHandlerId;
                    }
                }

                if (match(attributes))
                {
                    return new Element(attributes, handlers);
                }
            }
            else if (frame.FrameType == RenderTreeFrameType.Component && Find(frame.ComponentId, match) is { } inChild)
            {
                return inChild;
            }
        }

        return null;
    }

    private sealed record Element(IReadOnlyDictionary<string, object?> Attributes, IReadOnlyDictionary<string, ulong> Handlers);
}
