# Getting your files back

Read this before you need it. It takes five minutes now and none of the panic later.

You can restore your files without a Fortiq account, without Fortiq's servers, and without Fortiq
being installed. That is the point of the design: the tools in this package outlive the company that
wrote them.

---

## What you need

Three things. Any one of them missing and the others are not enough.

| | Where it is |
|---|---|
| **The repository** | Wherever you told Fortiq to put backups: a folder, an external drive, or an S3 bucket. |
| **The recovery kit** | The folder you chose as "recovery kit destination". It holds `kit.json` and two small `.cbor` files. |
| **Your 24 words** | On the paper you wrote them on. Fortiq does not have a copy. Nobody does. |

If you are on the PC that made the backup and it is still working, you may not need the words: that
machine can unlock its own repository. You will need them on any other machine.

---

## The easy way: on a working PC

1. Open Fortiq.
2. Choose **Recovery**.
3. Pick the backup, pick what to restore, choose where to put it.

Fortiq restores into a folder you choose. It does not write over your originals.

---

## On a computer that has never had Fortiq

You do not install anything.

1. Copy the `recover` folder from this package onto the machine — a USB stick is fine.
2. Make the repository reachable. A local folder or external drive: plug it in. S3: you will need
   your access key, secret key and region.
3. Copy your recovery kit folder onto the machine too.
4. Open a Command Prompt in the `recover` folder.

### Check that the repository is really there

```
Fortiq.Recover.exe inspect --repository <path-or-s3-url> --kit <kit-folder>
```

This asks the repository what it is and compares it with the kit. It changes nothing.

### See what backups exist

```
Fortiq.Recover.exe snapshots --repository <path-or-s3-url> --kit <kit-folder>
```

Each snapshot is one backup, with the date it was taken. Note the id of the one you want.

### Restore

```
Fortiq.Recover.exe restore --repository <path-or-s3-url> --kit <kit-folder> --snapshot <id> --target <empty-folder>
```

Choose an **empty** folder as the target. Fortiq will not restore over existing files.

### Where the 24 words go

Every command that has to open the repository will ask for your recovery words and wait. Type them
in order, separated by single spaces, and press Enter.

They are read from the keyboard on purpose — never from the command line, never from a file. A
command line ends up in your shell history and in the list of running processes, where anyone on the
machine can read it.

---

## If your backups are in S3

Set three environment variables in the same Command Prompt before running the commands above:

```
set AWS_ACCESS_KEY_ID=your-access-key
set AWS_SECRET_ACCESS_KEY=your-secret-key
set AWS_DEFAULT_REGION=your-region
```

The repository is the full URL Fortiq showed you, starting with `s3:` — for example
`s3:https://s3.eu-west-4.example.com/my-bucket`.

These keys reach your storage. They are not the keys to your data: what is in the bucket is
encrypted, and the 24 words are what decrypt it.

---

## When something is wrong

**"UnlockFailed"** — the repository would not open. Either the words are wrong, or the kit does not
belong to this repository, or this machine's own key is gone. Check the words first: order matters,
and so does spelling. Fortiq deliberately does not say which of the three failed, because saying so
would help somebody who is guessing.

**The kit and the repository disagree** — the kit is for a different repository. Find the right kit;
they are not interchangeable.

**"Repository busy"** — something else is using it right now. A backup may be running. Wait and try
again.

**The target folder is not empty** — choose an empty one. This is a refusal, not a failure: restoring
over files you still have is how a recovery turns into a loss.

---

## Two things worth doing today

**Check that you can read your own handwriting.** Take the paper with the 24 words and read them
back. If a word is ambiguous, rewrite it now, while the backup still works.

**Keep the words and the repository apart.** Words in a drawer and backups on a drive in the same
drawer means one burglary, one fire, one flood takes both.
