# Contactor

Control the relays of a Velleman VM201 ethernet relay board from your phone: turn things on and off, run them on
schedules, start several relays in order as one group (e.g. a fan heater: the fan first, then the heat, and the fan
running on after the heat goes off), with an interlock that keeps them in that order. Every change is logged with who
made it.

It is built for a workshop and for people who aren't technical: big buttons, few menus, and a page that works like an
app on the phone.

- **Runs** on a Raspberry Pi next to the board, with Docker Compose.
- **Sign-in** is done by Cloudflare Access (a one-time code by e-mail); no port is opened to the internet.
- **Built with** .NET 10, Blazor Server and SQLite. No JavaScript framework.

See `CLAUDE.md` for the architecture, the board's protocol and the domain rules.

## Development

Needs the .NET 10 SDK.

```sh
dotnet build Contactor.slnx
dotnet test --solution Contactor.slnx
```

Run everything locally with Aspire (the dashboard opens on http://localhost:15280):

```sh
dotnet run --project Contactor.AppHost
```

The AppHost starts the VM201 simulator on port 9760 and the web app against it, signed in as `john@example.com`. Stop
and start the simulator in the dashboard to test a lost connection to the board.

The tests run the VM201 client against the built-in simulator (`Vm201Simulator`) over localhost, so no hardware is
needed.

## Deploying

See [DEPLOY.md](DEPLOY.md): setting up Cloudflare, installing on the Raspberry Pi, nightly updates, backups and
publishing a release.

## Safety

The app's interlock only works while the Raspberry Pi and the app run. Where safety depends on it, e.g. heat without
the fan, wire it in the installation as well. Electrical work must be done by a qualified electrician.
