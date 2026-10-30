"""Prepare an optional, checksum-verified portable MSYS2 test runtime in .local only."""

from concurrent.futures import ThreadPoolExecutor
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile
import urllib.request

import zstandard


def main():
    repository = Path(__file__).resolve().parents[2]
    root = repository / ".local" / "terminal-runtime"
    cache = root / "packages"
    runtime = root / "msys"
    cache.mkdir(parents=True, exist_ok=True)
    runtime.mkdir(parents=True, exist_ok=True)
    packages = json.loads(Path(__file__).with_name("terminal-runtime-packages.json").read_text(encoding="utf-8"))

    def download(package):
        target = cache / package["file"]
        if not target.exists():
            urllib.request.urlretrieve("https://repo.msys2.org/msys/x86_64/" + package["file"], target)
        data = target.read_bytes()
        if hashlib.sha256(data).hexdigest() != package["sha256"]:
            raise ValueError("An MSYS2 test package did not match its pinned checksum: " + package["name"])
        return package["name"], data

    with ThreadPoolExecutor(max_workers=4) as workers:
        for name, data in workers.map(download, packages):
            raw = zstandard.ZstdDecompressor().decompress(data, max_output_size=100_000_000)
            with tarfile.open(fileobj=io.BytesIO(raw)) as archive:
                for member in archive.getmembers():
                    target = (runtime / member.name).resolve()
                    if not target.is_relative_to(runtime.resolve()):
                        raise ValueError("An MSYS2 test archive contains an unsafe path.")
                    # Links and installation hooks are unnecessary for this minimal runtime.
                    if member.isfile() or member.isdir():
                        archive.extract(member, runtime, filter="data")
            print("Prepared test package: " + name, flush=True)

    for folder in ("tmp", "dev", "home/test-user", "etc"):
        (runtime / folder).mkdir(parents=True, exist_ok=True)
    (runtime / "etc" / "nsswitch.conf").write_text("passwd: files\ngroup: files\n", encoding="utf-8")
    # MSYS2 needs a Windows SID mapping to allocate PTYs. Keep it solely in ignored local
    # output and replace display names and homes with a synthetic test identity.
    environment = {key: value for key, value in os.environ.items()
                   if key.upper() in ("SYSTEMROOT", "WINDIR", "COMSPEC", "TEMP", "TMP")}
    environment["PATH"] = str(runtime / "usr" / "bin")
    entries = subprocess.check_output([str(runtime / "usr" / "bin" / "mkpasswd.exe"), "-c"],
                                      env=environment, encoding="utf-8", timeout=15)
    entry = next(line for line in entries.splitlines() if line and not line.startswith("#"))
    fields = entry.split(":")
    sid = fields[4].rsplit(",", 1)[-1]
    fields[0], fields[4], fields[5], fields[6] = "test-user", "Test User," + sid, "/home/test-user", "/usr/bin/bash"
    (runtime / "etc" / "passwd").write_text(":".join(fields) + "\n", encoding="utf-8")
    (runtime / "etc" / "group").write_text("test-group:x:" + fields[3] + ":\n", encoding="utf-8")
    print("The isolated real-program terminal runtime is ready. No system installation was performed.")


if __name__ == "__main__":
    main()
