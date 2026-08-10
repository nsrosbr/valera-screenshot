Mutation payloads that cannot live inside mutate.ps1.

WHY THIS FOLDER EXISTS. PowerShell 5.1 reads .ps1 files as ANSI, so a single non-ASCII byte in
the script is a PARSER error - the script does not run at all. STD-ENC-01. This project has hit
that trap three times. Some mutations, however, MUST carry non-ASCII text: LOC-3 re-creates a bare
Cyrillic UI string, which is precisely the defect the LOC assertion guards against.

So the payload lives here as UTF-8 without BOM, and mutate.ps1 reads it with ReadAllText.
One file per side: <Id>.find.txt is matched exactly once in the target source, <Id>.repl.txt
replaces it. Trailing newlines are stripped by the reader, so an editor adding one is harmless.
