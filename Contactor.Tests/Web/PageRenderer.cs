using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

// A test renderer has to read the render tree, as bUnit does; the app itself never does.
#pragma warning disable BL0006

namespace Contactor.Tests.Web;

/// <summary>
/// Runs a page live, as a circuit would, with the app's services and one signed-in person: renders it, finds a button
/// by its attributes and presses it through the page's own handler. It counts the page's renders, so a test can tell
/// whether a change redrew it, and disposing it disposes the page. An element a page puts the cursor in is noted by its
/// <c>id</c> (<see cref="Focused"/>), as a browser would focus it.
/// </summary>
internal sealed class PageRenderer : Renderer
{
    private readonly AsyncServiceScope _scope;
    private readonly List<Exception> _errors = [];
    private readonly List<string?> _focused = [];

    // Each element reference the page captured, by its id, and the element's own id attribute; on the dispatcher.
    private readonly Dictionary<string, string?> _referenceIds = [];
    private int _renders;
    private int _pageRenders;
    private int _rootId;

    private PageRenderer(AsyncServiceScope scope)
        : base(scope.ServiceProvider, NullLoggerFactory.Instance)
    {
        _scope = scope;
        ElementReferenceContext = new WebElementReferenceContext(new FocusRecorder(this));
    }

    /// <summary>The <c>id</c> of each element the page put the cursor in, in order; null for one without an id.</summary>
    public IReadOnlyList<string?> Focused
    {
        get
        {
            lock (_focused)
            {
                return [.. _focused];
            }
        }
    }

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    /// <summary>How many times anything on the page was drawn.</summary>
    public int Renders => Volatile.Read(ref _renders);

    /// <summary>
    /// How many times the page itself was drawn, not counting its child components on their own (e.g. the tab bar
    /// loading its counts): for a test that a change didn't redraw the page.
    /// </summary>
    public int PageRenders => Volatile.Read(ref _pageRenders);

    /// <summary>What went wrong inside the page, which would have ended its live connection.</summary>
    public IReadOnlyList<Exception> Errors => _errors;

    /// <summary>Renders <typeparamref name="TPage"/> for <paramref name="email"/> (shown as <paramref name="name"/>).</summary>
    public static async Task<PageRenderer> StartAsync<TPage>(IServiceProvider app, string email, string name, IDictionary<string, object?>? parameters = null)
        where TPage : IComponent
    {
        var (renderer, rendered) = Start<TPage>(app, Task.FromResult(SignedIn(email, name)), parameters);
        await rendered;
        return renderer;
    }

    /// <summary>
    /// Starts <typeparamref name="TPage"/> while the person's sign-in waits for <paramref name="signIn"/>, so the page's
    /// first load is held until the test completes it. The task ends when the page has rendered, or failed to.
    /// </summary>
    public static (PageRenderer Page, Task Rendered) StartHeld<TPage>(IServiceProvider app, Task<AuthenticationState> signIn)
        where TPage : IComponent
    {
        return Start<TPage>(app, signIn, null);
    }

    /// <summary>The sign-in of <paramref name="email"/>, shown as <paramref name="name"/>.</summary>
    public static AuthenticationState SignedIn(string email, string name)
    {
        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Name, name)], "Test")));
    }

    private static (PageRenderer Page, Task Rendered) Start<TPage>(IServiceProvider app, Task<AuthenticationState> signIn, IDictionary<string, object?>? parameters)
        where TPage : IComponent
    {
        var scope = app.CreateAsyncScope();
        ((IHostEnvironmentAuthenticationStateProvider)scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>())
            .SetAuthenticationState(signIn);
        // As a circuit starts it: where the app is and the page that was opened. Without a browser, a page that goes
        // elsewhere throws NavigationException, which the test sees in Errors.
        if (scope.ServiceProvider.GetRequiredService<NavigationManager>() is IHostEnvironmentNavigationManager navigation)
        {
            navigation.Initialize("http://localhost/", "http://localhost/");
        }

        var renderer = new PageRenderer(scope);
        var rendered = renderer.Dispatcher.InvokeAsync(async () =>
        {
            var page = renderer.InstantiateComponent(typeof(TPage));
            renderer._rootId = renderer.AssignRootComponentId(page);
            await renderer.RenderRootComponentAsync(renderer._rootId, ParameterView.FromDictionary(parameters ?? new Dictionary<string, object?>()));
        });
        return (renderer, rendered);
    }

    /// <summary>Whether the page now has an element whose attributes match.</summary>
    public Task<bool> HasAsync(Func<IReadOnlyDictionary<string, object?>, bool> match)
    {
        return Dispatcher.InvokeAsync(() => Find(_rootId, match) is not null);
    }

    /// <summary>The page's text, its markup's and its child components', in order.</summary>
    public Task<string> TextAsync()
    {
        return Dispatcher.InvokeAsync(() =>
        {
            var text = new System.Text.StringBuilder();
            AppendText(_rootId, text);
            return text.ToString();
        });
    }

    /// <summary>Presses the first button whose attributes match, through its <c>onclick</c>.</summary>
    public Task ClickAsync(Func<IReadOnlyDictionary<string, object?>, bool> match)
    {
        return ClickAsync(match, 0);
    }

    /// <summary>Presses the button whose attributes match that comes <paramref name="index"/>th on the page, from 0.</summary>
    public async Task ClickAsync(Func<IReadOnlyDictionary<string, object?>, bool> match, int index)
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            var skip = index;
            var element = Find(_rootId, attributes => match(attributes) && skip-- == 0) ?? throw new InvalidOperationException("No such element on the page.");
            var handler = element.Handlers.TryGetValue("onclick", out var id) ? id : throw new InvalidOperationException($"It has no onclick: {string.Join(", ", element.Attributes.Select(a => $"{a.Key}={a.Value}"))}.");
            await DispatchEventAsync(handler, null, new MouseEventArgs());
        });
    }

    /// <summary>Sends the first form whose attributes match, through its <c>onsubmit</c>, as its submit button does.</summary>
    public async Task SubmitAsync(Func<IReadOnlyDictionary<string, object?>, bool> match)
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            var element = Find(_rootId, match) ?? throw new InvalidOperationException("No such element on the page.");
            var handler = element.Handlers.TryGetValue("onsubmit", out var id) ? id : throw new InvalidOperationException("It has no onsubmit.");
            await DispatchEventAsync(handler, null, EventArgs.Empty);
        });
    }

    /// <summary>Types <paramref name="value"/> into the first field whose attributes match, through its <c>oninput</c> or <c>onchange</c>.</summary>
    public async Task TypeAsync(Func<IReadOnlyDictionary<string, object?>, bool> match, object value)
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            var element = Find(_rootId, match) ?? throw new InvalidOperationException("No such element on the page.");
            var handler = element.Handlers.TryGetValue("oninput", out var id) || element.Handlers.TryGetValue("onchange", out id)
                ? id
                : throw new InvalidOperationException("It has no oninput or onchange.");
            await DispatchEventAsync(handler, null, new ChangeEventArgs { Value = value });
        });
    }

    /// <summary>A match on an element's own text, e.g. a button's "People".</summary>
    public static Func<IReadOnlyDictionary<string, object?>, bool> WithText(string text)
    {
        return attributes => attributes.TryGetValue(TextKey, out var found) && (found as string)?.Trim() == text;
    }

    /// <summary>An attribute match: the element's <paramref name="name"/> attribute is <paramref name="value"/>.</summary>
    public static Func<IReadOnlyDictionary<string, object?>, bool> With(string name, string value)
    {
        return attributes => attributes.TryGetValue(name, out var found) && found?.ToString() == value;
    }

    /// <summary>Gives the page new parameters, as a link to the same page with another route id does.</summary>
    public Task SetParametersAsync(IDictionary<string, object?> parameters)
    {
        return Dispatcher.InvokeAsync(() => RenderRootComponentAsync(_rootId, ParameterView.FromDictionary(parameters)));
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
        for (var i = 0; i < renderBatch.UpdatedComponents.Count; i++)
        {
            if (renderBatch.UpdatedComponents.Array[i].ComponentId == _rootId)
            {
                Interlocked.Increment(ref _pageRenders);
                break;
            }
        }

        // A new element's reference comes once, after its attributes: the browser would know it by that from now on.
        string? elementId = null;
        var frames = renderBatch.ReferenceFrames;
        for (var i = 0; i < frames.Count; i++)
        {
            ref var frame = ref frames.Array[i];
            switch (frame.FrameType)
            {
                case RenderTreeFrameType.Element:
                    elementId = null;
                    break;
                case RenderTreeFrameType.Attribute when frame.AttributeName == "id":
                    elementId = frame.AttributeValue?.ToString();
                    break;
                case RenderTreeFrameType.ElementReferenceCapture when !string.IsNullOrEmpty(frame.ElementReferenceCaptureId):
                    _referenceIds[frame.ElementReferenceCaptureId] = elementId;
                    break;
            }
        }

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

    private void AppendText(int componentId, System.Text.StringBuilder text)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        for (var i = 0; i < frames.Count; i++)
        {
            ref var frame = ref frames.Array[i];
            switch (frame.FrameType)
            {
                case RenderTreeFrameType.Text:
                    text.Append(frame.TextContent);
                    break;
                case RenderTreeFrameType.Markup:
                    text.Append(frame.MarkupContent);
                    break;
                case RenderTreeFrameType.Component:
                    AppendText(frame.ComponentId, text);
                    break;
            }
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

                // Its own words, under a name no attribute has (Text).
                var words = new System.Text.StringBuilder();
                for (var t = i + 1; t < i + frame.ElementSubtreeLength && t < frames.Count; t++)
                {
                    if (frames.Array[t].FrameType == RenderTreeFrameType.Text)
                    {
                        words.Append(frames.Array[t].TextContent);
                    }
                }

                attributes[TextKey] = words.ToString();
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

    // An element's own words, beside its attributes; not a name an attribute can have.
    private const string TextKey = " text";

    private sealed record Element(IReadOnlyDictionary<string, object?> Attributes, IReadOnlyDictionary<string, ulong> Handlers);

    // The browser's side of ElementReference.FocusAsync: notes which element, on the page's dispatcher.
    private sealed class FocusRecorder : IJSRuntime
    {
        private readonly PageRenderer _page;

        public FocusRecorder(PageRenderer page)
        {
            _page = page;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (args is [ElementReference element, ..])
            {
                lock (_page._focused)
                {
                    _page._focused.Add(_page._referenceIds.GetValueOrDefault(element.Id));
                }
            }

            return ValueTask.FromResult<TValue>(default!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            return InvokeAsync<TValue>(identifier, args);
        }
    }
}
