import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";

/** Creates a fresh tenant and operator key with the real CLI, so every run starts clean. */
export default function globalSetup() {
  const root = path.resolve(import.meta.dirname, "../..");
  // Prefer dotnet on PATH; fall back to the user-local install (~/.dotnet) the README suggests.
  const local = path.join(os.homedir(), ".dotnet", "dotnet");
  const dotnet = process.env.DOTNET ?? (existsSync(local) ? local : "dotnet");
  const out = execFileSync(dotnet, ["run", "--project", "tools/Relay.Cli", "--", "keys", "create", "--tenant", `e2e-${Date.now()}`], {
    cwd: root,
    encoding: "utf8",
  });
  const key = /API key:\s+(\S+)/.exec(out)?.[1];
  if (!key) throw new Error(`Could not create an API key:\n${out}`);
  mkdirSync(path.join(import.meta.dirname, ".auth"), { recursive: true });
  writeFileSync(path.join(import.meta.dirname, ".auth", "key"), key);
}
