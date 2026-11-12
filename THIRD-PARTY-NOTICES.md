# Third-party notices

Contactor is licensed under the MIT License (see `LICENSE`). It includes, or its container image bundles, the
following works of others, each under its own licence. The licence texts are in `licenses/` in the repository and in
`/licenses` in the image, next to `LICENSE` and this file.

## IBM Plex Sans and IBM Plex Mono

- Latin subsets of the fonts, served by the app and the simulator (`wwwroot/fonts` in `Contactor.Web` and
  `Contactor.Simulator`; `/app/wwwroot/fonts` in the image).
- Copyright 2019 IBM Corp. (IBM Plex Sans) and Copyright 2017 IBM Corp. (IBM Plex Mono).
- Licensed under the SIL Open Font License 1.1: `OFL.txt` next to the fonts.

## Font Awesome Free 7.3.1

- The relay group icons, copied as inline SVG into `Contactor.Web/Components/GroupIcon.razor`.
- Copyright 2026 Fonticons, Inc. (https://fontawesome.com).
- Icons licensed under Creative Commons Attribution 4.0 International (CC BY 4.0):
  https://creativecommons.org/licenses/by/4.0/ and https://fontawesome.com/license/free.

## cloudflared

- The Cloudflare Tunnel client, copied unchanged into the image (`/usr/local/bin/cloudflared`) from Cloudflare's
  `cloudflare/cloudflared` image.
- Copyright Cloudflare, Inc. (https://github.com/cloudflare/cloudflared).
- Licensed under the Apache License 2.0: `Apache-2.0.txt`. It has no NOTICE file. The binary also contains the Go
  libraries listed in its `go.mod`, under their own licences (mostly BSD-3-Clause and MIT); see the cloudflared source
  for the version in the `Dockerfile`.

## Packages in the published app

- **OpenTelemetry .NET** (`OpenTelemetry.*`): Copyright The OpenTelemetry Authors. Apache License 2.0:
  `Apache-2.0.txt`. Its own notices for code it includes: `OpenTelemetry-THIRD-PARTY-NOTICES.txt`.
- **Polly** (`Polly.Core`, `Polly.Extensions`, `Polly.RateLimiting`): Copyright (c) 2024, App vNext. BSD 3-Clause
  License: `Polly-BSD-3-Clause.txt`.
- **SQLitePCLRaw** (`SQLitePCLRaw.*`): Copyright 2014-2024 SourceGear, LLC. Apache License 2.0: `Apache-2.0.txt`.
  It bundles SQLite (`libe_sqlite3`), which is in the public domain.
- **.NET and the `Microsoft.*` packages:** Copyright (c) .NET Foundation and Contributors, and Microsoft Corporation;
  MIT License. The .NET runtime's own notices come with Microsoft's base image (`mcr.microsoft.com/dotnet/aspnet`).
