namespace Contactor.Web.Features.Identity;

/// <summary>
/// No sign-in: every request is let in, as one shared person, "Someone". Only with "Only on this network": the app is
/// then as private as that network, and the events can't tell people apart.
/// </summary>
public sealed class NoSignIn : ISignInMethod
{
    /// <summary>
    /// The shared person's e-mail: in a reserved domain (.invalid), so it can never be a real one. Its name, made from
    /// it on first sign-in as for anyone, is "Someone".
    /// </summary>
    public const string Email = "someone@contactor.invalid";

    public Task<string?> AuthenticateAsync(HttpContext context) => Task.FromResult<string?>(Email);
}
