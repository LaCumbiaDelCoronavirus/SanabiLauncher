using ReactiveUI.Fody.Helpers;
using SS14.Launcher.Api;
using SS14.Launcher.Localization;
using SS14.Launcher.Models.Data;

namespace SS14.Launcher.ViewModels.Login;

public class ResendConfirmationViewModel : BaseLoginViewModel
{
    private readonly AuthApi _authApi;
    private readonly LocalizationManager _loc = LocalizationManager.Instance;

    [Reactive] public string EditingEmail { get; set; } = "";
    [Reactive] public string EditingPrimaryAuthServer { get; set; } = SanabiAuthManager.DefaultEnterableAuthUrl;

    private bool _errored;

    public ResendConfirmationViewModel(MainWindowLoginViewModel parentVM, AuthApi authApi) : base(parentVM)
    {
        _authApi = authApi;
    }

    public async void SubmitPressed()
    {
        if (Busy)
            return;

        Busy = true;
        try
        {
            BusyText = _loc.GetString("login-resend-busy");
            var errors = await _authApi.ResendConfirmationAsync(EditingEmail, SanabiAuthManager.LazilyGetInfoFromUrl(EditingPrimaryAuthServer));

            _errored = errors != null;

            if (!_errored)
            {
                // This isn't an error lol but that's what I called the control.
                OverlayControl = new AuthErrorsOverlayViewModel(this, _loc.GetString("login-resend-success-title"), new[]
                {
                    _loc.GetString("login-resend-success-message")
                });
            }
            else
            {
                OverlayControl = new AuthErrorsOverlayViewModel(this, _loc.GetString("login-resend-error-title"), errors!);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    public override void OverlayOk()
    {
        if (_errored)
        {
            base.OverlayOk();
        }
        else
        {
            // If the overlay was a success overlay, switch back to login.
            ParentVM.SwitchToLogin();
        }
    }
}
