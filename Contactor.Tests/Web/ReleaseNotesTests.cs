using Contactor.Web.Features.Updates;

namespace Contactor.Tests.Web;

/// <summary>A release's notes, as "What's new" shows them: the list items, without the markdown's own characters.</summary>
public sealed class ReleaseNotesTests
{
    [Fact]
    public void Should_ReadTheRealNotes_IntoTheirChanges()
    {
        Assert.Equal(Changes2026_11_17_3, ReleaseNotes.Changes(Notes2026_11_17_3));
        Assert.Equal(Changes2026_11_17_2, ReleaseNotes.Changes(Notes2026_11_17_2));
        Assert.Equal(Changes2026_11_17, ReleaseNotes.Changes(Notes2026_11_17));
        Assert.Equal(Changes2026_11_16_3, ReleaseNotes.Changes(Notes2026_11_16_3));
    }

    [Fact]
    public void Should_ShowTheRealNotes_WithoutMarkdownCharacters()
    {
        var changes = ReleaseNotes.Changes(Notes2026_11_17_2);

        Assert.Equal(7, changes.Count);
        Assert.Equal(
            [("\"+ Board\" is gone from the board chips", true), (" on Relays, Events and Debug, and \"+ Add a board\" from the side menu, so nobody taps it by mistake. Add a board under ", false), ("Settings", true), (", where the boards are.", false)],
            ReleaseNotes.Parts(changes[0]));
        foreach (var part in changes.SelectMany(ReleaseNotes.Parts))
        {
            Assert.DoesNotContain("*", part.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("#", part.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("|", part.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("sha256", part.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("Full Changelog", part.Text, StringComparison.Ordinal);
        }
    }

    // The notes are made from the pull requests' titles, those labelled "internal" left out (.github/release.yml):
    // what is left besides the list is GitHub's own.
    [Fact]
    public void Should_KeepOnlyTheListedChanges()
    {
        var changes = ReleaseNotes.Changes(
            "## What's Changed\n### Fixes\n* Fix the clock by @someone-else in https://github.com/gonace/Contactor/pull/9\n- A plain line\n\n## New Contributors\n* @someone-else made their first contribution in https://github.com/gonace/Contactor/pull/9\n\n**Full Changelog**: https://x");

        Assert.Equal(["Fix the clock", "A plain line"], changes);
    }

    // The Release workflow writes an "Images" section at the end of the notes (Issue #314): a table and docker pull
    // lines, never changes.
    [Fact]
    public void Should_LeaveOutTheImagesSection()
    {
        var changes = ReleaseNotes.Changes(
            "## What's Changed\n* Fix the clock by @someone in https://github.com/gonace/Contactor/pull/9\n\n<!-- images -->\n## Images\n\n| Image | Version | Digest |\n|---|---|---|\n| [ghcr.io/gonace/contactor](https://github.com/gonace/Contactor/pkgs/container/contactor) | `2026.11.15` | `sha256:aa` |\n\n```sh\ndocker pull ghcr.io/gonace/contactor@sha256:aa\n```");

        Assert.Equal(["Fix the clock"], changes);
    }

    [Fact]
    public void Should_KeepTheTextOfLinksAndCode()
    {
        var changes = ReleaseNotes.Changes("* See [the guide](https://example.com/guide) and ![a picture](https://example.com/a.png)\n* Run `docker compose up` once");

        Assert.Equal(["See the guide and a picture", "Run docker compose up once"], changes);
    }

    // A link in a link's text: the list and the page show the same text.
    [Fact]
    public void Should_KeepOnlyTheText_OfALinkInALink()
    {
        var change = Assert.Single(ReleaseNotes.Changes("* See [[the guide](https://example.com/a)](https://example.com/b) now"));

        Assert.Equal("See the guide now", change);
        Assert.Equal([(change, false)], ReleaseNotes.Parts(change));
    }

    [Fact]
    public void Should_KeepTheTextOfLinks_WithParenthesesOrATitle()
    {
        var changes = ReleaseNotes.Changes(
            "* See [the page](https://en.wikipedia.org/wiki/Relay_(electric)) now\n* Read [the guide](https://example.com/guide \"The guide\") and [this](https://example.com 'This') and [that](https://example.com (That))");

        Assert.Equal(["See the page now", "Read the guide and this and that"], changes);
    }

    [Fact]
    public void Should_LeaveOutComments_CodeBlocks_AndHeadings()
    {
        var changes = ReleaseNotes.Changes(
            "# Title\n<!-- a comment\n* hidden in the comment\n-->\n* First <!-- inline --> change\n```\n* hidden in the code\n```\n~~~\n- hidden too\n~~~\n### * Not a change\n* Last change");

        Assert.Equal(["First change", "Last change"], changes);
    }

    // A code block ends only on a fence of its own character, at least as long as the one that opened it, with nothing
    // after it.
    [Theory]
    [InlineData("* Shown\n````\n```\n* Hidden\n````\n* Last")]
    [InlineData("* Shown\n~~~~\n~~~\n* Hidden\n~~~~~~\n* Last")]
    [InlineData("* Shown\n```\n```js\n* Hidden\n```\n* Last")]
    [InlineData("* Shown\n~~~\n~~~ text\n* Hidden\n~~~\n* Last")]
    public void Should_EndACodeBlock_OnlyOnAFenceAsLongAsItsOwn(string notes)
    {
        Assert.Equal(["Shown", "Last"], ReleaseNotes.Changes(notes));
    }

    [Fact]
    public void Should_ReadEachLine_WhateverItsLineEnd()
    {
        Assert.Equal(["One", "Two", "Three", "Four"], ReleaseNotes.Changes("* One\r* Two\r\n* Three\n* Four"));
    }

    [Theory]
    [InlineData("* ***Both***", "**Both**")]
    [InlineData("* a *** b", "a b")]
    [InlineData("* Start ***bold** end", "Start **bold** end")]
    public void Should_LeaveNoStrayAsterisk_AfterThreeOrMore(string notes, string expected)
    {
        var change = Assert.Single(ReleaseNotes.Changes(notes));

        Assert.Equal(expected, change);
        Assert.All(ReleaseNotes.Parts(change), p => Assert.DoesNotContain("*", p.Text, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("* Shown\n<!-- never closed\n* Hidden")]
    [InlineData("* Shown\n```\n* Hidden")]
    [InlineData("* Shown\n* Also <!-- never closed\n* Hidden")]
    public void Should_HideTheRest_AfterAnUnclosedCommentOrCodeBlock(string notes)
    {
        var changes = ReleaseNotes.Changes(notes);

        Assert.DoesNotContain(changes, c => c.Contains("Hidden", StringComparison.Ordinal));
        Assert.Equal("Shown", changes[0]);
    }

    // The page writes every part as text (UpdatePageTests), so HTML is only characters.
    [Fact]
    public void Should_KeepHtml_AsText()
    {
        Assert.Equal(["Fix <script>alert(1)</script> now"], ReleaseNotes.Changes("* Fix <script>alert(1)</script> now"));
    }

    [Theory]
    [InlineData("* **open", "open")]
    [InlineData("* a ** b ** c **", "a ** b ** c")]
    [InlineData("* ****", null)]
    [InlineData("* a****b", "ab")]
    [InlineData("* a ** ** b", "a b")]
    [InlineData("* **Both** and **open", "**Both** and open")]
    public void Should_KeepOnlyBoldWithAPartner(string notes, string? expected)
    {
        var changes = ReleaseNotes.Changes(notes);

        Assert.Equal(expected is null ? [] : [expected], changes);
        Assert.All(changes.SelectMany(ReleaseNotes.Parts), p => Assert.DoesNotContain("**", p.Text, StringComparison.Ordinal));
        Assert.All(changes.SelectMany(ReleaseNotes.Parts), p => Assert.False(p.Bold && string.IsNullOrWhiteSpace(p.Text)));
    }

    [Fact]
    public void Should_SplitAChange_IntoPlainAndBoldParts()
    {
        Assert.Equal([("Start", true), (" then plain", false)], ReleaseNotes.Parts("**Start** then plain"));
        Assert.Equal([("A ", false), ("middle", true), (" word", false)], ReleaseNotes.Parts("A **middle** word"));
        Assert.Equal([("At the ", false), ("end", true)], ReleaseNotes.Parts("At the **end**"));
        Assert.Equal([("Plain", false)], ReleaseNotes.Parts("Plain"));
    }

    // A line saved by an older version can still hold a ** without a partner, or an empty pair.
    [Fact]
    public void Should_LeaveOutAStrayBoldMarker_WhenDrawn()
    {
        Assert.Equal([("Both", true), (" and open", false)], ReleaseNotes.Parts("**Both** and **open"));
        Assert.Equal([("ab", false)], ReleaseNotes.Parts("a****b"));
        Assert.Empty(ReleaseNotes.Parts("**"));
    }

    // A line saved by an older version can still hold a link's markdown or a ***.
    [Fact]
    public void Should_LeaveOutALinksMarkdown_AndAStrayAsterisk_WhenDrawn()
    {
        Assert.Equal([("See the guide ", false), ("now", true)], ReleaseNotes.Parts("See [the guide](https://example.com/guide) **now**"));
        Assert.Equal([("Both", true)], ReleaseNotes.Parts("***Both***"));
    }

    [Fact]
    public void Should_EndALongChange_OnASentence()
    {
        var sentence = "This sentence is about fifty characters long, ok. ";
        var notes = "* " + string.Concat(Enumerable.Repeat(sentence, 20));

        var change = Assert.Single(ReleaseNotes.Changes(notes));

        Assert.True(change.Length <= ReleaseNotes.MaxChangeLength);
        Assert.EndsWith("ok.", change, StringComparison.Ordinal);
        Assert.Equal(string.Concat(Enumerable.Repeat(sentence, change.Length / sentence.Length + 1)).TrimEnd(), change);
    }

    // A sentence end early in the change would leave out most of it.
    [Fact]
    public void Should_EndALongChange_OnAWord_WhenItsOnlySentenceEndsEarly()
    {
        var notes = "* Fix. " + string.Join(' ', Enumerable.Repeat("word", 200));

        var change = Assert.Single(ReleaseNotes.Changes(notes));

        Assert.True(change.Length <= ReleaseNotes.MaxChangeLength);
        Assert.True(change.Length > ReleaseNotes.MaxChangeLength * 2 / 3);
        Assert.StartsWith("Fix. word", change, StringComparison.Ordinal);
        Assert.EndsWith("word…", change, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_EndALongChange_OnAWord_WithoutASentence()
    {
        var notes = "* " + string.Join(' ', Enumerable.Repeat("word", 1000));

        var change = Assert.Single(ReleaseNotes.Changes(notes));

        Assert.True(change.Length <= ReleaseNotes.MaxChangeLength);
        Assert.EndsWith("word…", change, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", change, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_LeaveOutAChange_WithNoWordThatFits()
    {
        Assert.Equal(["Next"], ReleaseNotes.Changes("* " + new string('a', 5000) + "\n* Next"));
    }

    [Fact]
    public void Should_ListAtMostThirty()
    {
        var notes = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"* Change {i}"));

        var changes = ReleaseNotes.Changes(notes);

        Assert.Equal(ReleaseNotes.MaxChanges, changes.Count);
        Assert.Equal("Change 30", changes[^1]);
    }

    [Fact]
    public void Should_LeaveOutWholeChanges_PastTheLimitForARelease()
    {
        var notes = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"* Change {i}: " + string.Join(' ', Enumerable.Repeat("words", 80))));

        var changes = ReleaseNotes.Changes(notes);

        Assert.True(changes.Sum(c => c.Length) <= ReleaseNotes.MaxLength);
        Assert.Equal(ReleaseNotes.MaxLength / changes[0].Length, changes.Count);
        Assert.All(changes, c => Assert.EndsWith("words", c, StringComparison.Ordinal));
    }

    // GitHub's list is at most 2 MB (GitHubReleases.MaxResponseBytes): one huge line, or very many, read in one pass.
    [Fact]
    public void Should_StayWithinTheLimits_OnHostileNotes()
    {
        var oneLine = "* " + string.Concat(Enumerable.Repeat("[[`** ](by @x in ", 50_000)) + string.Join(' ', Enumerable.Repeat("word", 100_000));
        var manyLines = string.Join('\n', Enumerable.Repeat("* A change. " + new string(' ', 100) + "by @x in https://x", 10_000));
        var spaces = "* a" + new string(' ', 1_000_000) + "b";

        var one = ReleaseNotes.Changes(oneLine);
        Assert.NotEmpty(one);
        Assert.All(one, c => Assert.True(c.Length <= ReleaseNotes.MaxChangeLength));
        var many = ReleaseNotes.Changes(manyLines);
        Assert.Equal(ReleaseNotes.MaxChanges, many.Count);
        Assert.All(many, c => Assert.Equal("A change.", c));
        Assert.Equal(["a"], ReleaseNotes.Changes(spaces));
    }

    // v2026.11.17.3's notes as GitHub sends them, with CRLF line endings.
    private static readonly string Notes2026_11_17_3 = """
        ## Check for updates now
        - **"Check now" in Settings → Updates** looks for a new version at once. Before, the app looked once a day, so a new release could take up to a day to show.
        - While it looks, the button reads "Checking…". Then the box shows what it found: a new version with "Update now", or that you have the newest.
        - It looks at most once a minute, and only once at a time, even when several people press it.

        **Full Changelog**: https://github.com/gonace/Contactor/compare/v2026.11.17.2...v2026.11.17.3

        <!-- images -->
        ## Images

        | Image | Version | Digest |
        |---|---|---|
        | [ghcr.io/gonace/contactor](https://github.com/gonace/Contactor/pkgs/container/contactor) | `2026.11.17.3` | `sha256:bd3bf3e7115a30017c0ba99b94903a3b69ddd1dc844a24e09fbd0fcb29125d9c` |
        | [ghcr.io/gonace/contactor-demo](https://github.com/gonace/Contactor/pkgs/container/contactor-demo) | `2026.11.17.3` | `sha256:ee432ee61534247d26bdfde1554bf8601405e489834a236e1e25a5df3c892b61` |

        ```sh
        docker pull ghcr.io/gonace/contactor@sha256:bd3bf3e7115a30017c0ba99b94903a3b69ddd1dc844a24e09fbd0fcb29125d9c
        docker pull ghcr.io/gonace/contactor-demo@sha256:ee432ee61534247d26bdfde1554bf8601405e489834a236e1e25a5df3c892b61
        ```
        """.ReplaceLineEndings("\r\n");

    private static readonly string[] Changes2026_11_17_3 =
    [
        "**\"Check now\" in Settings → Updates** looks for a new version at once. Before, the app looked once a day, so a new release could take up to a day to show.",
        "While it looks, the button reads \"Checking…\". Then the box shows what it found: a new version with \"Update now\", or that you have the newest.",
        "It looks at most once a minute, and only once at a time, even when several people press it.",
    ];

    // v2026.11.17.2's notes as GitHub sends them, with CRLF line endings.
    private static readonly string Notes2026_11_17_2 = """
        ## Add a board from Settings
        - **"+ Board" is gone from the board chips** on Relays, Events and Debug, and "+ Add a board" from the side menu, so nobody taps it by mistake. Add a board under **Settings**, where the boards are.
        - **With one board,** the row of board chips is hidden: there is nothing to choose. The side menu on a desktop still shows the board and why it doesn't answer.

        ## Safer groups
        - **A group relay the board refused is no longer logged as "On".** Before, the person's "On" was saved and the run became theirs, for a relay that stayed off. Now nothing changes, and the group's page says that the board isn't responding.

        ## Fixes
        - **Relays:** a page closed while it loads no longer keeps listening to the boards for as long as the app runs.
        - **Devantech ETH1610, ETH8020-B and ETH484-B:** a poll reads the analog inputs first and the relays last, so the relays' state, and the interlock that uses it, is always the newest. A board that has stopped answering no longer shows "No analog readings" first.
        - **Screen readers:** an analog input without a value says "No value", and the line under a relay starts with "Input".
        - Many small corrections from the reviews, and a test that keeps every transaction safe from a second writer.

        **Full Changelog**: https://github.com/gonace/Contactor/compare/v2026.11.17...v2026.11.17.2

        <!-- images -->
        ## Images

        | Image | Version | Digest |
        |---|---|---|
        | [ghcr.io/gonace/contactor](https://github.com/gonace/Contactor/pkgs/container/contactor) | `2026.11.17.2` | `sha256:638d0c2c211f0e02da23ebe0f6421f71fed0ca2e938f725d4b32690977e50450` |
        | [ghcr.io/gonace/contactor-demo](https://github.com/gonace/Contactor/pkgs/container/contactor-demo) | `2026.11.17.2` | `sha256:84b43a89d5d5e70b768f1a72a7f1b1544ad49d7073dded3977bc2caf72eb6549` |

        ```sh
        docker pull ghcr.io/gonace/contactor@sha256:638d0c2c211f0e02da23ebe0f6421f71fed0ca2e938f725d4b32690977e50450
        docker pull ghcr.io/gonace/contactor-demo@sha256:84b43a89d5d5e70b768f1a72a7f1b1544ad49d7073dded3977bc2caf72eb6549
        ```

        """.ReplaceLineEndings("\r\n");

    private static readonly string[] Changes2026_11_17_2 =
    [
        "**\"+ Board\" is gone from the board chips** on Relays, Events and Debug, and \"+ Add a board\" from the side menu, so nobody taps it by mistake. Add a board under **Settings**, where the boards are.",
        "**With one board,** the row of board chips is hidden: there is nothing to choose. The side menu on a desktop still shows the board and why it doesn't answer.",
        "**A group relay the board refused is no longer logged as \"On\".** Before, the person's \"On\" was saved and the run became theirs, for a relay that stayed off. Now nothing changes, and the group's page says that the board isn't responding.",
        "**Relays:** a page closed while it loads no longer keeps listening to the boards for as long as the app runs.",
        "**Devantech ETH1610, ETH8020-B and ETH484-B:** a poll reads the analog inputs first and the relays last, so the relays' state, and the interlock that uses it, is always the newest. A board that has stopped answering no longer shows \"No analog readings\" first.",
        "**Screen readers:** an analog input without a value says \"No value\", and the line under a relay starts with \"Input\".",
        "Many small corrections from the reviews, and a test that keeps every transaction safe from a second writer.",
    ];

    // v2026.11.17's notes as GitHub sends them, with CRLF line endings.
    private static readonly string Notes2026_11_17 = """
        ## Analog inputs
        - **The app now shows analog inputs:** temperature, pressure, a tank's level, or anything a sensor gives as a voltage. Each one is a card under Inputs on Relays, with its value and unit, e.g. "21.5 °C".
        - **Its own page:** a name, the unit (°C, %, bar, V or your own) and a scale from two points: "the board reads 0 V = −20 °C, 5 V = 80 °C". No label on the sensor? Put a thermometer next to it and press "Use the reading now".
        - **No decimals to choose:** the app shows as many as the board can really measure.
        - **Boards:** Velleman VM204, Devantech ETH1610, ETH8020-B and ETH484-B, and Denkovi DAEnetIP2 v2. Without a sensor, or above the board's range, the card says so in plain words.
        - **The relays come first:** reading an analog input never delays or stops switching a relay. A sensor that doesn't answer only shows "No reading".

        ## See what controls a relay
        - **A relay's or a group's card shows each input that controls it,** e.g. "Door: on when it opens", so it's clear why a relay you turned off came back on.

        ## Groups
        - **New group: several relays in one step,** the same as on the group's page, e.g. two fans that start together.

        ## Settings and menus
        - **On a desktop:** Settings has two columns, and Settings sits at the bottom of the side menu.
        - The "Simplest" tag sits at the right of a choice's title.

        ## Fixes
        - Many small corrections from the reviews, and tests that sometimes failed now pass every time.

        **Full Changelog**: https://github.com/gonace/Contactor/compare/v2026.11.16.3...v2026.11.17


        <!-- images -->
        ## Images

        | Image | Version | Digest |
        |---|---|---|
        | [ghcr.io/gonace/contactor](https://github.com/gonace/Contactor/pkgs/container/contactor) | `2026.11.17` | `sha256:892c4927a6a6dd6faa9a36378978357c89a5d54dac0d948141a53e7323a79a87` |
        | [ghcr.io/gonace/contactor-demo](https://github.com/gonace/Contactor/pkgs/container/contactor-demo) | `2026.11.17` | `sha256:01139aefba501d2b65b4864e454bbcbbecc1ac0cac437b847201264a79560ae8` |

        ```sh
        docker pull ghcr.io/gonace/contactor@sha256:892c4927a6a6dd6faa9a36378978357c89a5d54dac0d948141a53e7323a79a87
        docker pull ghcr.io/gonace/contactor-demo@sha256:01139aefba501d2b65b4864e454bbcbbecc1ac0cac437b847201264a79560ae8
        ```

        """.ReplaceLineEndings("\r\n");

    private static readonly string[] Changes2026_11_17 =
    [
        "**The app now shows analog inputs:** temperature, pressure, a tank's level, or anything a sensor gives as a voltage. Each one is a card under Inputs on Relays, with its value and unit, e.g. \"21.5 °C\".",
        "**Its own page:** a name, the unit (°C, %, bar, V or your own) and a scale from two points: \"the board reads 0 V = −20 °C, 5 V = 80 °C\". No label on the sensor? Put a thermometer next to it and press \"Use the reading now\".",
        "**No decimals to choose:** the app shows as many as the board can really measure.",
        "**Boards:** Velleman VM204, Devantech ETH1610, ETH8020-B and ETH484-B, and Denkovi DAEnetIP2 v2. Without a sensor, or above the board's range, the card says so in plain words.",
        "**The relays come first:** reading an analog input never delays or stops switching a relay. A sensor that doesn't answer only shows \"No reading\".",
        "**A relay's or a group's card shows each input that controls it,** e.g. \"Door: on when it opens\", so it's clear why a relay you turned off came back on.",
        "**New group: several relays in one step,** the same as on the group's page, e.g. two fans that start together.",
        "**On a desktop:** Settings has two columns, and Settings sits at the bottom of the side menu.",
        "The \"Simplest\" tag sits at the right of a choice's title.",
        "Many small corrections from the reviews, and tests that sometimes failed now pass every time.",
    ];

    // v2026.11.16.3's notes as GitHub sends them, with CRLF line endings.
    private static readonly string Notes2026_11_16_3 = """
        ## See why a board doesn't answer
        - **The app now says why it can't connect,** where the board can tell: a board of another model ("The board at this address is an ETH484-B, not an ETH008-B."), a password or login that is needed, or a wrong password or login. It shows in the setup guide, "Add a board", Settings, the side menu and on Relays.
        - **A wrong model:** "Choose model" takes you back to the model step. **A missing password:** the cursor goes to Password.
        - A board that doesn't answer at all shows the same text as before.

        ## Safer groups
        - **"Turn off" for a group** now also turns off a relay that was just turned back on, in the right order from the bottom.
        - **Turning one relay of a group off** while it, or a relay below it, is still being turned on now turns the relays below off first, and the step above keeps its own run-on.

        ## Fixes
        - A board that stops answering right after a command no longer looks connected for one poll too long.
        - Removing a board, or stopping the app, waits for a command under way instead of letting it hang. The VM204 and VM208 do the same.
        - Screenshots, docs and many small corrections from earlier reviews.

        **Full Changelog**: https://github.com/gonace/Contactor/compare/v2026.11.16.2...v2026.11.16.3

        <!-- images -->
        ## Images

        | Image | Version | Digest |
        |---|---|---|
        | [ghcr.io/gonace/contactor](https://github.com/gonace/Contactor/pkgs/container/contactor) | `2026.11.16.3` | `sha256:73e0f690228c9c6e3697c74ec152482ae4ddf903a2be64bd683798828cfa4d9e` |
        | [ghcr.io/gonace/contactor-demo](https://github.com/gonace/Contactor/pkgs/container/contactor-demo) | `2026.11.16.3` | `sha256:102f8621f5225b17650665da3873f19644f998038084efc353877a9fe52bddf1` |

        ```sh
        docker pull ghcr.io/gonace/contactor@sha256:73e0f690228c9c6e3697c74ec152482ae4ddf903a2be64bd683798828cfa4d9e
        docker pull ghcr.io/gonace/contactor-demo@sha256:102f8621f5225b17650665da3873f19644f998038084efc353877a9fe52bddf1
        ```

        """.ReplaceLineEndings("\r\n");

    private static readonly string[] Changes2026_11_16_3 =
    [
        "**The app now says why it can't connect,** where the board can tell: a board of another model (\"The board at this address is an ETH484-B, not an ETH008-B.\"), a password or login that is needed, or a wrong password or login. It shows in the setup guide, \"Add a board\", Settings, the side menu and on Relays.",
        "**A wrong model:** \"Choose model\" takes you back to the model step. **A missing password:** the cursor goes to Password.",
        "A board that doesn't answer at all shows the same text as before.",
        "**\"Turn off\" for a group** now also turns off a relay that was just turned back on, in the right order from the bottom.",
        "**Turning one relay of a group off** while it, or a relay below it, is still being turned on now turns the relays below off first, and the step above keeps its own run-on.",
        "A board that stops answering right after a command no longer looks connected for one poll too long.",
        "Removing a board, or stopping the app, waits for a command under way instead of letting it hang. The VM204 and VM208 do the same.",
        "Screenshots, docs and many small corrections from earlier reviews.",
    ];
}
