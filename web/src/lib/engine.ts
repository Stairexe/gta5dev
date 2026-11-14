// Loads the .NET format engine (CodeWalker.Core compiled to WebAssembly) and exposes its exports.
// Every file is built locally in the browser; nothing is uploaded.

interface EngineExports {
  ClearFiles(): void;
  AddFile(field: string, data: Uint8Array): void;
  LastReport(): string;
  LastError(): string;
  BuildProp(spec: string): Uint8Array | null;
  BuildPed(spec: string): Uint8Array | null;
  Inspect(fileName: string): string | null;
}

let loading: Promise<EngineExports> | null = null;

export function engineStatus(): 'idle' | 'loading' | 'ready' {
  return ready ? 'ready' : loading ? 'loading' : 'idle';
}
let ready = false;

export function loadEngine(): Promise<EngineExports> {
  if (!loading) {
    loading = (async () => {
      const url = `${import.meta.env.BASE_URL}engine/_framework/dotnet.js`;
      const { dotnet } = await import(/* @vite-ignore */ url);
      const runtime = await dotnet.withConfig({ disableIntegrityCheck: false }).create();
      const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
      ready = true;
      return exports.Engine as EngineExports;
    })();
    loading.catch(() => { loading = null; });
  }
  return loading;
}

/** Runs a build with the given staged files; throws with the engine's message on failure. */
export async function withFiles<T>(files: [string, Blob][], run: (e: EngineExports) => T | null): Promise<{ result: T; report: string }> {
  const e = await loadEngine();
  e.ClearFiles();
  for (const [field, blob] of files) e.AddFile(field, new Uint8Array(await blob.arrayBuffer()));
  // Yield so the UI can paint "Building…" before the synchronous build starts.
  await new Promise(r => setTimeout(r, 30));
  const result = run(e);
  e.ClearFiles();
  if (result == null) throw new Error(e.LastError() || 'Build failed.');
  return { result, report: e.LastReport() };
}
