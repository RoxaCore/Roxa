# Roxa — licensing notes

The README keeps a short summary of each point; the full wording lives
here. The licence text itself is in [`LICENSE`](LICENSE), the per-component
list is in [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt), and how to
report a vulnerability is in [`SECURITY.md`](SECURITY.md).

## The name and logo are not covered

The GPL gives you the right to run, study, modify and redistribute the
*code*. It does not transfer the **"Roxa" name, the logo, or the visual
identity** of this project — those remain with the Roxa project and are not
licensed under the GPL.

In practice that means:

- You may fork this code, change it, and distribute your own build.
- You must keep the GPL notices intact and make your own source available.
- You must **not** call your build "Roxa", and must **not** reuse the Roxa
  icon or branding, unless you have written permission from the project.
- If you redistribute an unmodified build, you may keep calling it Roxa.

This is the trademark reservation permitted by section 7(e) of the GPL. It
protects users from mistaking an unofficial build for the real one; it does
not restrict what you do with the code.

## Third-party components

Third-party components keep their own licenses and are listed in
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt): Xray-core is MPL-2.0,
Wintun carries the WireGuard Prebuilt Binaries License, both fonts are
SIL OFL-1.1, and every managed dependency is MIT. The GPL and these licenses
coexist because Roxa runs those components as **separate programs**, not by
linking them into a single work.

## Country flag images

The flags in `Assets/Flags/*.png` (90 files) are artwork from
[Twemoji](https://github.com/jdecked/twemoji) v17.0.3, licensed
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) and credited to
Jason Sofonia, Justine De Caires, Twitter, Inc. and the other Twemoji
contributors. Each PNG was cropped to its content and centred on a
transparent 96x72 canvas so that the images share one size in the server
list; the artwork itself was not recoloured or redrawn. Roxa claims no
rights over these files, and they are not covered by the GPL that covers
the code. The full licence text ships beside them as
`Assets/Flags/CC-BY-4.0.txt`, with the source and attribution recorded in
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt), section 6.
