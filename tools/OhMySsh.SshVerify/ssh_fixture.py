"""Ephemeral loopback SSH fixture; never reads the user's SSH configuration."""

import asyncio
import json
from pathlib import Path
import secrets
import sys

import asyncssh


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
    host_key = asyncssh.generate_private_key("ssh-ed25519")

    class Server(asyncssh.SSHServer):
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
            if username == "slow":
                await asyncio.sleep(30)
            return username in ("qa", "slow") and secrets.compare_digest(supplied, password)

        def public_key_auth_supported(self):
            return True

        def validate_public_key(self, username, supplied):
            return username == "qa" and supplied in (client_key.convert_to_public(), rsa_key.convert_to_public())

    async def start_server(port, key):
        return await asyncssh.create_server(
            Server, "127.0.0.1", port, server_host_keys=[key],
            password_auth=True, public_key_auth=True, kbdint_auth=False,
            login_timeout=40, reuse_address=True,
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
    port = server.get_port()
    stall_server = await asyncio.start_server(stall, "127.0.0.1", 0)
    stall_port = stall_server.sockets[0].getsockname()[1]

    def respond(value):
        print(json.dumps(value), flush=True)

    respond({"Port": port, "StallPort": stall_port, "Password": password,
             "Passphrase": passphrase, "PlainKeyPath": str(plain_path),
             "EncryptedKeyPath": str(encrypted_path),
             "RsaKeyPath": str(rsa_path),
             "Fingerprint": host_key.get_fingerprint("sha256")})
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
            elif command == "rotate":
                for connection in tuple(connections):
                    connection.abort()
                server.close()
                await server.wait_closed()
                host_key = asyncssh.generate_private_key("ssh-ed25519")
                server = await start_server(port, host_key)
                respond({"Fingerprint": host_key.get_fingerprint("sha256")})
            elif command == "stats":
                respond({"Connections": len(connections), "Stalled": len(stalled_writers)})
            else:
                respond({"Error": "Unknown fixture command"})
    finally:
        for connection in tuple(connections):
            connection.abort()
        for writer in tuple(stalled_writers):
            writer.close()
        server.close()
        stall_server.close()
        await server.wait_closed()
        await stall_server.wait_closed()
        plain_path.unlink(missing_ok=True)
        encrypted_path.unlink(missing_ok=True)
        rsa_path.unlink(missing_ok=True)


if __name__ == "__main__":
    asyncio.run(main())
