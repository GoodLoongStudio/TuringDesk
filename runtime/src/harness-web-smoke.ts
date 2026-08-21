import { spawn } from 'node:child_process'
import { existsSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
// The smoke test is compiled to runtime/dist, while pnpm installs the official
// Harness package in runtime/node_modules. Keep this resolver valid both for the
// compiled CI entrypoint and for direct source-side execution.
const runtimeRoots = [resolve(here, '..'), here]
const dshBin = runtimeRoots
  .map(root => join(root, 'node_modules', '@deepseek-ai', 'dsh', 'lib', 'bin.js'))
  .find(candidate => existsSync(candidate))
const port = Number(process.env.TURINGDESK_HARNESS_WEB_SMOKE_PORT ?? '4329')
const url = `http://127.0.0.1:${port}/`

if (!dshBin) {
  throw new Error(`Official DeepSeek Harness CLI is missing from the runtime roots: ${runtimeRoots.join(', ')}`)
}

const home = mkdtempSync(join(tmpdir(), 'turingdesk-dsh-web-'))
let output = ''
let child: ReturnType<typeof spawn> | undefined

try {
  child = spawn(process.execPath, [
    dshBin,
    '--profile', 'web',
    '--host', '127.0.0.1',
    '--port', String(port),
  ], {
    cwd: home,
    env: {
      ...process.env,
      DSH_HOME: home,
      DSH_TELEMETRY_DISABLED: '1',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  })

  child.stdout?.setEncoding('utf8')
  child.stderr?.setEncoding('utf8')
  child.stdout?.on('data', chunk => { output = keepTail(output + String(chunk)) })
  child.stderr?.on('data', chunk => { output = keepTail(output + String(chunk)) })

  await waitUntilReady(child, url, 45_000)

  // Verify the WebUI serves real HTML content, not just a 200 status.
  const body = await fetchResponseBody(url)
  if (!body.includes('<html') && !body.includes('<!DOCTYPE')) {
    throw new Error(`DeepSeek Harness WebUI did not return HTML. Body preview: ${body.slice(0, 200)}`)
  }

  console.log(`Official DeepSeek Harness WebUI smoke passed: ${url}`)
} finally {
  if (child && child.exitCode === null) {
    child.kill('SIGTERM')
    await Promise.race([
      new Promise<void>(resolveExit => child?.once('exit', () => resolveExit())),
      delay(5_000),
    ])
    if (child.exitCode === null) child.kill('SIGKILL')
  }
  rmSync(home, { recursive: true, force: true })
}

async function waitUntilReady(process: ReturnType<typeof spawn>, target: string, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs
  let lastError = 'not ready'

  while (Date.now() < deadline) {
    if (process.exitCode !== null) {
      throw new Error(`DeepSeek Harness WebUI exited before ready (code ${String(process.exitCode)}).\n${output}`)
    }

    try {
      const response = await fetch(target, { signal: AbortSignal.timeout(1_500) })
      if (response.ok) return
      lastError = `HTTP ${response.status}`
    } catch (error) {
      lastError = error instanceof Error ? error.message : String(error)
    }
    await delay(350)
  }

  throw new Error(`DeepSeek Harness WebUI did not become ready at ${target}: ${lastError}\n${output}`)
}

async function fetchResponseBody(target: string): Promise<string> {
  const response = await fetch(target, { signal: AbortSignal.timeout(3_000) })
  if (!response.ok) {
    throw new Error(`WebUI returned HTTP ${response.status} when fetching body`)
  }
  return await response.text()
}

function keepTail(value: string): string {
  const limit = 16_384
  return value.length <= limit ? value : value.slice(value.length - limit)
}

function delay(ms: number): Promise<void> {
  return new Promise(resolveDelay => setTimeout(resolveDelay, ms))
}
