`mcmod-index.json` is a read-only format conversion of the repository's existing WPF
`Plain Craft Launcher 2/Resources/mcmod.buf`. It preserves the 25,146 wiki IDs,
Chinese names and provider slugs; no new online database is scraped. Existing
project/data attribution continues to apply.

Source SHA-256: `1cc190effba8922d556bfadc61d62e76b80d48439e4945b862760bfdd3a07b4c`.
Reproduce with `python3 scripts/conduit/Extract-McmodIndex.py`.
The desktop uses a provider-specific exact slug match for mod/datapack wiki links;
unknown projects never receive an invented wiki ID.
