# Third-party notices

Contactor is licensed under the MIT License (see `LICENSE`). It includes, or its image bundles, the following works of
others, each under its own licence.

## IBM Plex Sans and IBM Plex Mono

- Fonts served by the app and the simulator (`Contactor.Web/wwwroot/fonts`, `Contactor.Simulator/wwwroot/fonts`).
- Copyright © 2017 IBM Corp., with Reserved Font Name "Plex".
- Licensed under the SIL Open Font License 1.1: see `OFL.txt` next to the fonts.

## Font Awesome Free

- The relay group icons, copied as inline SVG into `Contactor.Web/Components/GroupIcon.razor`.
- Copyright © 2026 Fonticons, Inc. (https://fontawesome.com).
- Icons licensed under Creative Commons Attribution 4.0 International (CC BY 4.0):
  https://creativecommons.org/licenses/by/4.0/ and https://fontawesome.com/license/free.

## cloudflared

- The Cloudflare Tunnel client, copied unchanged into the container image (`/usr/local/bin/cloudflared`) from
  Cloudflare's `cloudflare/cloudflared` image.
- Copyright © Cloudflare, Inc. (https://github.com/cloudflare/cloudflared).
- Licensed under the Apache License 2.0: see `licenses/Apache-2.0.txt`.

## .NET and NuGet packages

- The image runs on Microsoft's .NET runtime (`mcr.microsoft.com/dotnet/aspnet`) and includes the NuGet packages the app
  references (see `Directory.Packages.props`), each under its own licence, mostly MIT. Their licences come with the
  packages and the base image.

In the image, this file, `LICENSE` and the `licenses` folder are in `/licenses`.
