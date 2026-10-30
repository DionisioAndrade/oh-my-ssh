"""Isolated SSH terminal fixtures: byte protocol checks and optional real programs."""

import asyncio
import os
from pathlib import Path
import secrets
import subprocess

import asyncssh
from asyncssh.stream import SSHServerStreamSession


class RoutedTerminal(SSHServerStreamSession):
    """Keep SFTP and a raw interactive shell available on the same SSH connection."""

    def __init__(self, terminal, sftp_factory=None):
        super().__init__(None, sftp_factory, sftp_version=3)
        self.terminal = terminal
        self.shell_active = False

    def connection_made(self, channel):
        super().connection_made(channel)
        self.terminal.connection_made(channel)

    def pty_requested(self, term_type, term_size, term_modes):
        return self.terminal.pty_requested(term_type, term_size, term_modes)

    def shell_requested(self):
        self.shell_active = self.terminal.shell_requested()
        return self.shell_active

    def session_started(self):
        if self.shell_active:
            self.terminal.session_started()
        else:
            super().session_started()

    def terminal_size_changed(self, width, height, pixwidth, pixheight):
        self.terminal.terminal_size_changed(width, height, pixwidth, pixheight)
        if not self.shell_active:
            super().terminal_size_changed(width, height, pixwidth, pixheight)

    def data_received(self, data, datatype):
        if self.shell_active:
            self.terminal.data_received(data, datatype)
        else:
            super().data_received(data, datatype)

    def connection_lost(self, exception):
        self.terminal.connection_lost(exception)
        super().connection_lost(exception)


class ProtocolTerminal(asyncssh.SSHServerSession):
    """Deterministic protocol probes, deliberately not a shell or a VT emulator."""

    def __init__(self, sessions):
        self.sessions = sessions
        self.channel = None
        self.pending = bytearray()
        self.tasks = set()
        self.size = (0, 0)
        self.term_type = ""

    def connection_made(self, channel):
        self.channel = channel
        self.sessions.add(self)

    def pty_requested(self, term_type, term_size, term_modes):
        self.term_type = term_type
        self.size = term_size[:2]
        return True

    def shell_requested(self):
        return bool(self.term_type)

    def session_started(self):
        columns, rows = self.size
        self.channel.write(f"FIXTURE_READY {self.term_type} {columns} {rows}\r\n".encode())

    def terminal_size_changed(self, width, height, pixwidth, pixheight):
        self.size = (width, height)

    def data_received(self, data, datatype):
        self.pending.extend(data)
        while b"\r" in self.pending:
            line, _, remainder = self.pending.partition(b"\r")
            self.pending[:] = remainder
            self.handle_line(bytes(line))

    def handle_line(self, line):
        if line == b"fixture-fragments":
            # Split a UTF-8 code point, CSI, truecolor SGR, and alternate-screen controls.
            chunks = [b"FRAGMENTS ", b"caf\xc3", b"\xa9 \xe7", b"\xa9\xba \xf0\x9f", b"\x8c\x8d\r\n",
                      b"\x1b[", b"38;2;12;34;56m", b"COLOR", b"\x1b[0m\r\n",
                      b"\x1b[?1049", b"h\x1b[2J\x1b[H", b"ALTERNATE", b"\x1b[?1049l",
                      b"\r\nFRAGMENTS_DONE\r\n"]
            task = asyncio.create_task(self.write_fragments(chunks))
            self.tasks.add(task)
            task.add_done_callback(self.tasks.discard)
        elif line == b"fixture-size":
            self.channel.write(f"SIZE {self.size[0]} {self.size[1]}\r\n".encode())
        elif line == b"fixture-close":
            self.channel.write(b"SHELL_EXIT\r\n")
            self.channel.exit(0)
        elif line in (b"fixture-burst", b"fixture-overflow"):
            task = asyncio.create_task(self.write_burst(line == b"fixture-burst"))
            self.tasks.add(task)
            task.add_done_callback(self.tasks.discard)
        else:
            self.channel.write(b"INPUT " + line.hex().encode() + b"\r\n")

    async def write_fragments(self, chunks):
        for chunk in chunks:
            self.channel.write(chunk)
            await asyncio.sleep(0.015)

    async def write_burst(self, paced):
        for _ in range(128):
            self.channel.write(b"x" * 16384)
            await asyncio.sleep(0.004 if paced else 0)
        self.channel.write(b"\r\nBURST_DONE\r\n")

    def connection_lost(self, exception):
        self.sessions.discard(self)
        for task in self.tasks:
            task.cancel()


def real_runtime_available(runtime):
    binary = Path(runtime) / "usr" / "bin"
    return bool(runtime) and all((binary / name).is_file() for name in
                                 ("bash.exe", "tmux.exe", "vim.exe", "socat.exe", "stty.exe"))


class RealTerminal(asyncssh.SSHServerSession):
    """Bridge an owned POSIX PTY to SSH without another terminal emulator in between."""

    def __init__(self, sessions, runtime, directory):
        self.sessions = sessions
        self.runtime = Path(runtime)
        self.directory = Path(directory)
        self.channel = None
        self.process = None
        self.task = None
        self.size = (80, 24)
        self.term_type = "xterm-256color"
        self.socket_name = "qa" + secrets.token_hex(12)
        self.pty_path = "/tmp/" + self.socket_name + ".pty"
        self.script_path = self.runtime / "tmp" / (self.socket_name + ".sh")
        self.pid_path = self.runtime / "tmp" / (self.socket_name + ".pid")
        self.child_environment = None

    def connection_made(self, channel):
        self.channel = channel
        self.sessions.add(self)

    def pty_requested(self, term_type, term_size, term_modes):
        self.term_type = term_type
        self.size = term_size[:2]
        return True

    def shell_requested(self):
        return True

    def session_started(self):
        binary = self.runtime / "usr" / "bin"
        # Child-only environment: no user dotfiles, profile directories, or shell history.
        self.child_environment = {
            key: value for key, value in os.environ.items()
            if key.upper() in ("SYSTEMROOT", "WINDIR", "COMSPEC", "TEMP", "TMP")
        }
        self.child_environment.update({
            "PATH": str(binary), "HOME": "/home/test-user", "USER": "test-user", "LOGNAME": "test-user",
            "SHELL": "/usr/bin/bash", "LANG": "C.UTF-8", "LC_ALL": "C.UTF-8", "TERM": self.term_type,
            "MSYSTEM": "MSYS", "MSYS2_PATH_TYPE": "strict", "HISTFILE": "/dev/null", "PS1": "fixture$ ",
            "TMUX_TMPDIR": "/tmp", "FIXTURE_TMUX_SOCKET": self.socket_name,
        })
        self.script_path.write_text(
            f"/usr/bin/stty cols {self.size[0]} rows {self.size[1]}\n"
            f"printf '%s' \"$$\" > /tmp/{self.pid_path.name}\n"
            "export PS1='fixture$ '\n"
            "exec /usr/bin/bash --noprofile --norc -i\n", encoding="utf-8", newline="\n",
        )
        self.process = subprocess.Popen(
            [str(binary / "socat.exe"), "-t", "0.5", "-b", "32768", "-",
             f"EXEC:/usr/bin/bash --noprofile --norc /tmp/{self.script_path.name},pty,stderr,setsid,ctty,link={self.pty_path}"],
            cwd=self.directory, env=self.child_environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW,
        )
        self.task = asyncio.create_task(self.pump())

    async def pump(self):
        try:
            while True:
                data = await asyncio.to_thread(self.process.stdout.read1, 32768)
                if not data:
                    self.channel.exit(0)
                    return
                self.channel.write(data)
        except (EOFError, OSError):
            if self.channel:
                self.channel.exit(0)

    def data_received(self, data, datatype):
        if self.process:
            self.process.stdin.write(data)
            self.process.stdin.flush()

    def terminal_size_changed(self, width, height, pixwidth, pixheight):
        self.size = (width, height)
        if self.process:
            subprocess.run(
                [str(self.runtime / "usr" / "bin" / "stty.exe"), "-F", self.pty_path,
                 "cols", str(width), "rows", str(height)],
                env=self.child_environment, cwd=self.directory, stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                timeout=5, creationflags=subprocess.CREATE_NO_WINDOW, check=True,
            )

    def connection_lost(self, exception):
        self.sessions.discard(self)
        if self.task:
            self.task.cancel()
        if self.process:
            # The unique socket belongs to this channel; never target another tmux server.
            try:
                subprocess.run(
                    [str(self.runtime / "usr" / "bin" / "tmux.exe"), "-L", self.socket_name, "kill-server"],
                    env=self.child_environment, cwd=self.directory,
                    stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    timeout=5, creationflags=subprocess.CREATE_NO_WINDOW, check=False,
                )
            finally:
                if self.pid_path.exists():
                    pid = int(self.pid_path.read_text(encoding="utf-8"))
                    if pid > 1:
                        subprocess.run(
                            [str(self.runtime / "usr" / "bin" / "kill.exe"), "-HUP", str(pid)],
                            env=self.child_environment, cwd=self.directory, stdin=subprocess.DEVNULL,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                            timeout=5, creationflags=subprocess.CREATE_NO_WINDOW, check=False,
                        )
                self.process.stdin.close()
                try:
                    self.process.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    self.process.terminate()
                    self.process.wait(timeout=5)
                self.process.stdout.close()
                self.script_path.unlink(missing_ok=True)
                self.pid_path.unlink(missing_ok=True)
                (self.runtime / "tmp" / (self.socket_name + ".pty")).unlink(missing_ok=True)
