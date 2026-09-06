=====================================================
 Fortiq Community Edition (Windows x64)
 Backups that prove they can be restored
=====================================================

WHAT IS IN THIS PACKAGE

  desktop\     the Fortiq application and its installer
  service\     the background service that runs scheduled backups
  recover\     Fortiq.Recover, the standalone recovery tool
  LICENSE      Apache License 2.0
  RECOVERY-GUIDE.md    how to get your files back - read this before you need it
  SECURITY.md          how to report a security problem
  SHA256SUMS           the hash of every file above
  bundle-manifest.json what this package is, and the hash of every file it installs

  This build is not code-signed. Windows will warn you when you run it.


QUICK START

  1. Open the "desktop" folder and run Fortiq.Desktop.exe.
  2. Choose "Install Fortiq" to set it up on this PC, or "Run as Portable"
     to use it without installing anything.
  3. Follow the wizard to protect your first folder.

  Installing puts Fortiq in C:\Program Files\Fortiq and registers a background
  service, so backups run on schedule even when the application is closed.
  Portable does neither: backups run only while Fortiq is open.

  Installing adds Fortiq to the Start menu. Portable does not: run it from
  this package's desktop folder.


THE 24 WORDS ARE THE BACKUP OF YOUR BACKUP

  Fortiq shows you 24 recovery words once, while protecting a folder. Write them
  on paper, in order, and keep them somewhere other than this computer.

  Without them, a lost PC means lost backups. With them, your files come back on
  any Windows machine - no Fortiq account, no Fortiq server, no installation.

  RECOVERY-GUIDE.md in this package explains exactly how.


GETTING YOUR FILES BACK

  On this PC:            open Fortiq and choose Recovery.
  On a different PC:     see RECOVERY-GUIDE.md. You need the recovery kit
                         folder, your 24 words, and the recover\ folder here.


  https://github.com/cybou-fr/Fortiq
