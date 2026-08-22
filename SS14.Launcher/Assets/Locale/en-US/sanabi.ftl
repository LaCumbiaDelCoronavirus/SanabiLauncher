
account-drop-down-switch-no-account = Switch to no account
account-drop-down-refresh-tokens = Refresh all account tokens
account-drop-down-busy-refreshing-tokens = Refreshing all tokens...

add-favorite-window-button-add = Add
# 'Example' address shown as a watermark in the address input box
add-favorite-window-example-address = ss14://example.com

connecting-ok = Ok

# 'Example' address shown as a watermark in the address input box
direct-connect-address-example = ss14://example.com:1212

hub-settings-button-save = Save

hub-settings-button-help = ?
hub-settings-checkbox-enable-default = Enable default hub(s)
# 'Example' address shown as a watermark in the custom hub address input box
hub-settings-address-example = https://example.com/hub/

## Strings for the "register" view on login

login-register-title = Register
login-register-username-watermark = Username
login-register-email-watermark = Email Address
login-register-password-watermark = Password
login-register-confirm-password-watermark = Confirm Password
login-register-age-checkbox = I am 13 years of age or older
login-register-button-submit = Register
login-register-button-switch-to-login = Log into an existing account instead
login-register-error-username-empty = Username is empty
login-register-error-username-too-long = Username is too long
login-register-error-username-too-short = Username is too short
login-register-error-username-invalid-char = Username contains an invalid character
login-register-error-unknown = ???
login-register-error-email-empty = Email is empty
login-register-error-email-invalid = Email is invalid
login-register-error-password-empty = Password is empty
login-register-error-password-mismatch = Confirm password does not match
login-register-error-age = You must be 13 or older
login-register-busy-registering = Registering account...
login-register-error-title = Unable to register
login-register-busy-logging-in = Logging in...

## Strings for the "resend confirmation email" view on login

login-resend-title = Resend email confirmation
login-resend-message = If you've managed to... misplace your original confirmation email, you can send another one here by entering your email address.
login-resend-email-watermark = Your email address
login-resend-button-submit = Submit
login-resend-button-back = Back to login
login-resend-busy = Resending email...
login-resend-success-title = Confirmation email sent
login-resend-success-message = A confirmation email has been sent to your email address.
login-resend-error-title = Error

main-window-out-of-date-warning-desc = A new version of the launcher exists and this message is only here because it's probably worth downloading! You may either download the new version at the given link or dismiss this warning.
main-window-out-of-date-dismiss = Fuck off

main-window-busy-endpoint-init = Doing endpoint initialisation
main-window-login-proceed-logged-out = Proceed logged-out and query hub

tab-servers-no-hubs-title = You have no hub APIs set!
tab-servers-no-hubs-desc =
    You have no hub APIs specified in your settings to use, or none of them work. This means no servers will show up here. This can be changed in your settings.
    Also this UI looks pretty terrible so please remind me to change it.

server-entry-dns-error = DNS-ERR

tab-home-file-picker-title = Select replay or content bundle file
tab-home-file-picker-filter-replay-or-bundle = Replay or content bundle files

tab-options-restart-message = The launcher will restart now.
tab-options-show-changelog = Show changelog
tab-options-show-changelog-desc = Shows the changelog overlay on the home page, as if the launcher were out of date.

## Strings for the "Sanabi" tab

tab-sanabi-title = Sanabi
tab-sanabi-heading-patching = Patching
tab-sanabi-enable-patching = Enable patching
tab-sanabi-enable-patching-desc = Enable any kind of patching? Inherently enables engine patching.
tab-sanabi-patch-content = Patch content
tab-sanabi-patch-content-desc = If checked, then both engine and content will be patched. Otherwise, only patches engine. Obviously only applies if patching is enabled.
tab-sanabi-hwid-patch = Enable HWID spoofing patch
tab-sanabi-hwid-patch-desc = Patches out HWID to be spoofed when connecting to a server. Only applies if engine patching is enabled. Based on the account's seed configured in the config.
tab-sanabi-fullscreen-patch = Borderless windowed as fullscreen patch
tab-sanabi-fullscreen-patch-desc = Patches out RT's implementation of fullscreen and converts it to be borderless windowed. Only applies if engine patching is enabled. May fix your screen flickering when moving out of focus. Buggy on non-Windows systems.
tab-sanabi-userdata-virt = UserData Virtualisation
tab-sanabi-userdata-virt-desc = Why? SS14 client-side server content has read+write access to a UserData directory, which can be used to save data across sessions, and also be used against *you*. Enabling this will prevent writes to this directory from being saved to the disk, however reads can still happen. This can break some things if they havent been saved to disk yet, like parallax caches.
tab-sanabi-heading-external-mods = External mods
tab-sanabi-enable-external-mods = Enable external mods
tab-sanabi-enable-external-mods-desc = If patching is enabled, do we patch external mods; those that are in the mod's mod directory? Zip-file mods larger than 45MiB uncompressed will not be extracted fully and will most likely crash the game upon load.
tab-sanabi-external-mods-warning = Loaded external mods are not checked for malware and therefore may pose a risk to your computer; enable external mods at your own risk.
tab-sanabi-selected-external-mods = Selected external mods
tab-sanabi-rescan-mods = Rescan mods
tab-sanabi-mod-directory = Mod Directory
tab-sanabi-heading-spoofing = Spoofing
tab-sanabi-heading-fingerprint = Fingerprint
tab-sanabi-pass-fingerprint = Pass launcher fingerprint
tab-sanabi-pass-fingerprint-desc = Should the launcher pass it's fingerprint to everything via http? Turning this off may be sus.
tab-sanabi-spoof-fingerprint = Spoof launcher fingerprint
tab-sanabi-spoof-fingerprint-desc = If passing the launcher fingerprint, do we create and use new, spoofed random one every time the launcher is started?
tab-sanabi-heading-hwid = HWID
tab-sanabi-send-hwid = Send HWID to server
tab-sanabi-send-hwid-desc = When connecting to a server, willingly send HWID to Robust Auth? Modern servers require this otherwise you get disconnected with an appropriate message. Different from patching out the HWID.
tab-sanabi-seed-desc = Seed to use when generating spoofed HWID and spoofed fingerprint. This is interpreted as an unsigned 64-bit integer [ulong]; it is 20 digits at maximum.
tab-sanabi-regenerate-seed = Regenerate account seed
tab-sanabi-heading-misc = Misc.
tab-sanabi-start-on-login-menu = Start launcher on login menu
tab-sanabi-start-on-login-menu-desc = Should the launcher start on the login menu (where no external API has yet been queried) or on the homepage (where hub API is likely to be queried). Useless 98% of the time.
tab-sanabi-ping-servers = Ping servers in dropdown
tab-sanabi-ping-servers-desc = Should server ping be determined when opening their dropdown? Some ABSOLUTELY 'SESSED servers might use this to detect you. Note that not all servers will support being pinged via ICMP, so most of those which don't accept ICMP pings will usually give TimedOut as the error reason.
tab-sanabi-randomise-ping-delay = Randomise ping-query delay
tab-sanabi-randomise-ping-delay-desc = When you ping a server, multiple pings are done. There is a delay between those ping queries. When this is off, the delay is uniform (always the same). When this is on, the delay is randomised each time. SOMEONE asked me to code this. This will give a flat increase to how long it takes to ping a server, usually.
tab-sanabi-heading-visual = Visual
tab-sanabi-acrylic-blur = Use acrylic blur effect for homepage
tab-sanabi-acrylic-blur-desc = Forcefully disable transitioning images and use acrylic blur background on the homepage.
tab-sanabi-heading-debug = Debug
tab-sanabi-debug-mode = Debug mode
tab-sanabi-debug-mode-desc = Debug mode for Harmony. If on, a dump of IL logs will be generated and added to your desktop when the game is launched.
tab-sanabi-wait-for-debugger = Wait for debugger to attach before starting game
tab-sanabi-wait-for-debugger-desc = After launching the game process, should the game wait for a debugger to attach before doing anything? Only use if you know what you're doing.
tab-sanabi-open-data-directory = Open Launcher Data Directory
