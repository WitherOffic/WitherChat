# WitherChat privacy and network behavior

This document describes the application, not the independent policies of
Twitch, Google/YouTube, DonationAlerts, BetterTTV, 7TV or their CDNs.

## Network features

WitherChat contacts chat providers for the channels/accounts selected by
the user. Depending on enabled features, requests include login and token
validation, chat reading/sending, channel/user/stream metadata, moderation,
stream events, donation alerts, avatars, badges, emotes and other media.

Saved account/channel selections can reconnect automatically. Media and
metadata can be fetched in the background for enabled features. Anonymous
Twitch viewing also requires requests to Twitch and media services.
The receiving service can observe network metadata such as the client's
IP address, plus the information needed for the requested operation.

OAuth grants and moderation actions are sent to the corresponding service.
Private credentials must not be published with source or log exports.

## Local data

Settings, stored sessions, local chat logs, diagnostics, moderation caches
and saved stream moments are kept in the application's local profile.
Chat logs and exported files can contain usernames, message contents and
timestamps. Users are responsible for choosing where exports are saved
and whether to share them.

The application does not configure a project-owned telemetry/analytics
collector or automatically upload these local files to the maintainer.
Third-party services have their own data handling rules; this statement
does not promise that those services retain no data.

The OBS overlay is a local service intended for a user-configured browser
source. Do not expose it to untrusted networks.

## Controls and removal

Disconnect accounts/channels or disable a feature to stop using that
integration. Close the app through its Exit command to stop its background
connections. Deleting a portable EXE does not erase local profile data.
Remove profile data only after saving any logs/settings you want to keep.

Provider policies should be reviewed on the corresponding official sites:
Twitch, Google/YouTube, DonationAlerts, BetterTTV and 7TV.

## Development and reporting

Do not attach access/refresh tokens, sessions, widget credentials, signing
tokens or private logs to a public issue. Reports should use sanitized
screenshots and minimal non-sensitive examples.
