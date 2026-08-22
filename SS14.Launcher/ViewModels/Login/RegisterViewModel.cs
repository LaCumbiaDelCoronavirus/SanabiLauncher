using System;
using System.Diagnostics;
using System.Net.Mail;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Robust.Shared.AuthLib;
using SS14.Launcher.Api;
using SS14.Launcher.Localization;
using SS14.Launcher.Models.Data;
using SS14.Launcher.Models.Logins;

namespace SS14.Launcher.ViewModels.Login;

public class RegisterViewModel : BaseLoginViewModel
{
    private readonly DataManager _cfg;
    private readonly AuthApi _authApi;
    private readonly LoginManager _loginMgr;
    private readonly LocalizationManager _loc = LocalizationManager.Instance;

    [Reactive] public string EditingUsername { get; set; } = "";
    [Reactive] public string EditingPassword { get; set; } = "";
    [Reactive] public string EditingPasswordConfirm { get; set; } = "";
    [Reactive] public string EditingEmail { get; set; } = "";
    [Reactive] public string EditingPrimaryAuthServer { get; set; } = SanabiAuthManager.DefaultEnterableAuthUrl;

    [Reactive] public bool IsInputValid { get; private set; }
    [Reactive] public string InvalidReason { get; private set; } = " ";

    [Reactive] public bool Is13OrOlder { get; set; }


    public RegisterViewModel(MainWindowLoginViewModel parentVm, DataManager cfg, AuthApi authApi, LoginManager loginMgr)
        : base(parentVm)
    {
        _cfg = cfg;
        _authApi = authApi;
        _loginMgr = loginMgr;

        this.WhenAnyValue(x => x.EditingUsername, x => x.EditingPassword, x => x.EditingPasswordConfirm,
                x => x.EditingEmail, x => x.Is13OrOlder)
            .Subscribe(UpdateInputValid);
    }

    private void UpdateInputValid((string user, string pass, string passConfirm, string email, bool is13OrOlder) s)
    {
        var (user, pass, passConfirm, email, is13OrOlder) = s;

        IsInputValid = false;
        if (!UsernameHelpers.IsNameValid(user, out var reason))
        {
            InvalidReason = reason switch
            {
                UsernameHelpers.UsernameInvalidReason.Empty => _loc.GetString("login-register-error-username-empty"),
                UsernameHelpers.UsernameInvalidReason.TooLong => _loc.GetString("login-register-error-username-too-long"),
                UsernameHelpers.UsernameInvalidReason.TooShort => _loc.GetString("login-register-error-username-too-short"),
                UsernameHelpers.UsernameInvalidReason.InvalidCharacter => _loc.GetString("login-register-error-username-invalid-char"),
                _ => _loc.GetString("login-register-error-unknown")
            };
            return;
        }

        if (string.IsNullOrEmpty(email))
        {
            InvalidReason = _loc.GetString("login-register-error-email-empty");
            return;
        }

        if (!MailAddress.TryCreate(email, out _))
        {
            InvalidReason = _loc.GetString("login-register-error-email-invalid");
            return;
        }

        if (string.IsNullOrEmpty(pass))
        {
            InvalidReason = _loc.GetString("login-register-error-password-empty");
            return;
        }

        if (pass != passConfirm)
        {
            InvalidReason = _loc.GetString("login-register-error-password-mismatch");
            return;
        }

        if (!is13OrOlder)
        {
            InvalidReason = _loc.GetString("login-register-error-age");
            return;
        }

        InvalidReason = " ";
        IsInputValid = true;
    }

    public async void OnRegisterInButtonPressed()
    {
        if (!IsInputValid || Busy)
        {
            return;
        }

        BusyText = _loc.GetString("login-register-busy-registering");
        Busy = true;
        try
        {
            var authInfo = SanabiAuthManager.LazilyGetInfoFromUrl(EditingPrimaryAuthServer);
            var result = await _authApi.RegisterAsync(EditingUsername, EditingEmail, EditingPassword, authInfo);
            if (!result.IsSuccess)
            {
                OverlayControl = new AuthErrorsOverlayViewModel(this, _loc.GetString("login-register-error-title"), result.Errors);
                return;
            }

            var status = result.Status;
            if (status == RegisterResponseStatus.Registered)
            {
                BusyText = _loc.GetString("login-register-busy-logging-in");
                // No confirmation needed, log in immediately.
                var request = new AuthApi.AuthenticateRequest(EditingUsername, EditingPassword, authInfo);
                var resp = await _authApi.AuthenticateAsync(request);

                await LoginViewModel.DoLogin(this, request, resp, _loginMgr, _authApi);

                _cfg.CommitConfig();
            }
            else
            {
                Debug.Assert(status == RegisterResponseStatus.RegisteredNeedConfirmation);

                ParentVM.SwitchToRegisterNeedsConfirmation(EditingUsername, EditingPassword);
            }
        }
        finally
        {
            Busy = false;
        }
    }
}
