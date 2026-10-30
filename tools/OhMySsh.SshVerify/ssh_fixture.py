"""Ephemeral loopback SSH fixture; never reads the user's SSH configuration."""

import asyncio
import json
from pathlib import Path
import secrets
import sys

import asyncssh
from terminal_fixture import ProtocolTerminal, RealTerminal, RoutedTerminal, real_runtime_available


async def main():
    directory = Path(sys.argv[1]).resolve()
    directory.mkdir(parents=True, exist_ok=True)
    password = secrets.token_urlsafe(24)
    passphrase = secrets.token_urlsafe(24)
    client_key = asyncssh.generate_private_key("ssh-ed25519")
    rsa_key = asyncssh.generate_private_key("ssh-rsa", key_size=2048)
    plain_path = directory / "fixture_ed25519"
    encrypted_path = directory / "fixture_ed25519_encrypted"
    rsa_path = directory / "fixture_rsa.pem"
    plain_path.write_bytes(client_key.export_private_key("openssh"))
    encrypted_path.write_bytes(client_key.export_private_key("openssh", passphrase=passphrase))
    rsa_path.write_bytes(rsa_key.export_private_key("pkcs1-pem"))
    connections = set()
    stalled_writers = set()
    terminal_sessions = set()
    real_terminal_sessions = set()
    runtime = sys.argv[2] if len(sys.argv) > 2 else ""
    (directory / "vim-fixture.txt").write_text("VIM_FIXTURE_INITIAL\n", encoding="utf-8")
    host_key = asyncssh.generate_private_key("ssh-ed25519")
    initial_host_key = host_key
    sftp_root = directory / "sftp-root"
    sftp_root.mkdir()
    for name in ("empty", "Unicode folder", "denied", "no-replace"):
        (sftp_root / name).mkdir()
    (sftp_root / "hello.txt").write_bytes(b"SFTP fixture contents\n")
    (sftp_root / "açúcar 空 白.txt").write_bytes("Unicode payload — SFTP\n".encode("utf-8"))
    (sftp_root / "empty.bin").touch()
    (sftp_root / "no-replace" / "existing.txt").write_bytes(b"preserve this destination")
    with (sftp_root / "large.bin").open("wb") as large:
        for _ in range(512):
            large.write(bytes(range(256)) * 256)
    slow_io = False
    slow_auth = False

    class SFTP(asyncssh.SFTPServer):
        def __init__(self, channel):
            super().__init__(channel, chroot=str(sftp_root))

        def map_path(self, path):
            # Denials are deterministic on Windows, where POSIX chmod is not sufficient.
            if b"denied" in path.split(b"/"):
                raise asyncssh.SFTPPermissionDenied("Fixture permission denied")
            return super().map_path(path)

        async def read(self, file_obj, offset, size):
            if slow_io:
                await asyncio.sleep(0.02)
            return super().read(file_obj, offset, size)

        async def write(self, file_obj, offset, data):
            if slow_io:
                await asyncio.sleep(0.02)
            return super().write(file_obj, offset, data)

        def posix_rename(self, oldpath, newpath):
            if newpath.startswith(b"/no-replace/"):
                raise asyncssh.SFTPOpUnsupported("Fixture atomic replacement disabled")
            return super().posix_rename(oldpath, newpath)

    class Server(asyncssh.SSHServer):
        def __init__(self, enable_sftp=True, real_terminal=False):
            self.enable_sftp = enable_sftp
            self.real_terminal = real_terminal

        def session_requested(self):
            terminal = RealTerminal(real_terminal_sessions, runtime, directory) if self.real_terminal else ProtocolTerminal(terminal_sessions)
            return RoutedTerminal(terminal, SFTP if self.enable_sftp else None)

        def connection_made(self, connection):
            self.connection = connection
            connections.add(connection)

        def connection_lost(self, exception):
            connections.discard(self.connection)

        def begin_auth(self, username):
            return True

        def password_auth_supported(self):
            return True

        async def validate_password(self, username, supplied):
            if username == "slow" or slow_auth:
                await asyncio.sleep(30)
            return username in ("qa", "slow") and secrets.compare_digest(supplied, password)

        def public_key_auth_supported(self):
            return True

        def validate_public_key(self, username, supplied):
            return username == "qa" and supplied in (client_key.convert_to_public(), rsa_key.convert_to_public())

    async def start_server(port, key, enable_sftp=True, real_terminal=False):
        return await asyncssh.create_server(
            lambda: Server(enable_sftp, real_terminal), "127.0.0.1", port, server_host_keys=[key],
            password_auth=True, public_key_auth=True, kbdint_auth=False,
            login_timeout=40, reuse_address=True,
            encoding=None, line_editor=False,
        )

    async def stall(reader, writer):
        stalled_writers.add(writer)
        try:
            await reader.read()
        finally:
            stalled_writers.discard(writer)
            writer.close()
            await writer.wait_closed()

    server = await start_server(0, host_key)
    retired_servers = []
    port = server.get_port()
    no_sftp_server = await start_server(0, host_key, enable_sftp=False)
    real_server = await start_server(0, host_key, enable_sftp=False, real_terminal=True) if real_runtime_available(runtime) else None
    stall_server = await asyncio.start_server(stall, "127.0.0.1", 0)
    stall_port = stall_server.sockets[0].getsockname()[1]

    def respond(value):
        print(json.dumps(value), flush=True)

    respond({"Port": port, "StallPort": stall_port, "NoSftpPort": no_sftp_server.get_port(), "SftpRoot": str(sftp_root), "Password": password,
             "Passphrase": passphrase, "PlainKeyPath": str(plain_path),
             "EncryptedKeyPath": str(encrypted_path),
             "RsaKeyPath": str(rsa_path),
             "Fingerprint": host_key.get_fingerprint("sha256"),
             "RealTerminalPort": real_server.get_port() if real_server else 0})
    try:
        while True:
            line = await asyncio.to_thread(sys.stdin.readline)
            if not line:
                break
            command = line.strip()
            if command == "stop":
                respond({"Stopped": True})
                break
            if command == "drop":
                for connection in tuple(connections):
                    connection.abort()
                respond({"Dropped": True})
            elif command == "slow-on":
                slow_io = True
                respond({"Slow": True})
            elif command == "slow-off":
                slow_io = False
                respond({"Slow": False})
            elif command == "slow-auth-on":
                slow_auth = True
                respond({"SlowAuth": True})
            elif command == "slow-auth-off":
                slow_auth = False
                respond({"SlowAuth": False})
            elif command in ("rotate-listener", "restore-listener"):
                # Preserve established SSH sessions while changing the identity used by new connections.
                server.close()
                retired_servers.append(server)
                host_key = asyncssh.generate_private_key("ssh-ed25519") if command == "rotate-listener" else initial_host_key
                server = await start_server(port, host_key)
                respond({"Fingerprint": host_key.get_fingerprint("sha256")})
            elif command == "rotate":
                for connection in tuple(connections):
                    connection.abort()
                server.close()
                await server.wait_closed()
                host_key = asyncssh.generate_private_key("ssh-ed25519")
                server = await start_server(port, host_key)
                respond({"Fingerprint": host_key.get_fingerprint("sha256")})
            elif command == "stats":
                respond({"Connections": len(connections), "Stalled": len(stalled_writers),
                         "TerminalSessions": len(terminal_sessions), "RealTerminalSessions": len(real_terminal_sessions)})
            else:
                respond({"Error": "Unknown fixture command"})
    finally:
        for connection in tuple(connections):
            connection.abort()
        for writer in tuple(stalled_writers):
            writer.close()
        server.close()
        no_sftp_server.close()
        stall_server.close()
        if real_server:
            real_server.close()
        await server.wait_closed()
        for retired_server in retired_servers:
            await retired_server.wait_closed()
        await no_sftp_server.wait_closed()
        await stall_server.wait_closed()
        if real_server:
            await real_server.wait_closed()
        plain_path.unlink(missing_ok=True)
        encrypted_path.unlink(missing_ok=True)
        rsa_path.unlink(missing_ok=True)


if __name__ == "__main__":
    asyncio.run(main())
