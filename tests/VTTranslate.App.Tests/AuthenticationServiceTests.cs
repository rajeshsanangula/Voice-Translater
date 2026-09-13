using VTTranslate.App.Authentication;

namespace VTTranslate.App.Tests;

public class AuthenticationServiceTests
{
    [Fact]
    public async Task SignInAsync_Success_TransitionsToSignedIn()
    {
        var tokenProvider = new FakeTokenProvider();
        tokenProvider.SilentTokens.Enqueue("token");
        var service = new AuthenticationService(tokenProvider);

        var states = new List<AuthenticationState>();
        service.StateChanged += (_, s) => states.Add(s);

        await service.SignInAsync();

        Assert.Equal(AuthenticationState.SignedIn, service.State);
        Assert.Contains(AuthenticationState.Authenticating, states);
        Assert.Contains(AuthenticationState.SignedIn, states);
    }

    [Fact]
    public async Task SignInAsync_AlreadySignedIn_IsANoOp_DoesNotReacquire()
    {
        var tokenProvider = new FakeTokenProvider();
        tokenProvider.SilentTokens.Enqueue("token");
        var service = new AuthenticationService(tokenProvider);
        await service.SignInAsync();

        await service.SignInAsync(); // second call

        Assert.Equal(1, tokenProvider.SilentCallCount); // never re-acquired
    }

    [Fact]
    public async Task SignInAsync_InteractiveCancelled_TransitionsToAuthenticationFailed()
    {
        var tokenProvider = new FakeTokenProvider { SilentRequiresInteractive = true, ThrowOnNextInteractive = true };
        var service = new AuthenticationService(tokenProvider);

        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => service.SignInAsync());
        Assert.Equal(AuthenticationState.AuthenticationFailed, service.State);
    }

    [Fact]
    public async Task TrySilentSignInAsync_NoCachedAccount_StaysSignedOut_NeverThrows()
    {
        var tokenProvider = new FakeTokenProvider { HasCachedAccount = false };
        var service = new AuthenticationService(tokenProvider);

        await service.TrySilentSignInAsync();

        Assert.Equal(AuthenticationState.SignedOut, service.State);
        Assert.Equal(0, tokenProvider.InteractiveCallCount); // never launches a browser
    }

    [Fact]
    public async Task TrySilentSignInAsync_CachedAccountButSilentFails_StaysSignedOut_NeverLaunchesInteractive()
    {
        var tokenProvider = new FakeTokenProvider { HasCachedAccount = true, SilentRequiresInteractive = true };
        var service = new AuthenticationService(tokenProvider);

        await service.TrySilentSignInAsync();

        Assert.Equal(AuthenticationState.SignedOut, service.State);
        Assert.Equal(0, tokenProvider.InteractiveCallCount); // the whole point of "silent-only"
    }

    [Fact]
    public async Task TrySilentSignInAsync_CachedAccountWithValidToken_SignsIn()
    {
        var tokenProvider = new FakeTokenProvider { HasCachedAccount = true };
        tokenProvider.SilentTokens.Enqueue("cached-token");
        var service = new AuthenticationService(tokenProvider);

        await service.TrySilentSignInAsync();

        Assert.Equal(AuthenticationState.SignedIn, service.State);
    }

    [Fact]
    public async Task SignOutAsync_ClearsState_AndDelegatesToTokenProvider()
    {
        var tokenProvider = new FakeTokenProvider();
        tokenProvider.SilentTokens.Enqueue("token");
        var service = new AuthenticationService(tokenProvider);
        await service.SignInAsync();

        await service.SignOutAsync();

        Assert.Equal(AuthenticationState.SignedOut, service.State);
        Assert.False(tokenProvider.HasCachedAccount);
    }
}
