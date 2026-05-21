import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { PiDbgStatusBar } from './statusBar';

export class PiDbgDebugAdapterDescriptorFactory
    implements vscode.DebugAdapterDescriptorFactory {

    private readonly _context: vscode.ExtensionContext;
    private readonly _statusBar: PiDbgStatusBar;

    constructor(context: vscode.ExtensionContext, statusBar: PiDbgStatusBar) {
        this._context   = context;
        this._statusBar = statusBar;
    }

    createDebugAdapterDescriptor(
        session: vscode.DebugSession,
        _executable: vscode.DebugAdapterExecutable | undefined
    ): vscode.ProviderResult<vscode.DebugAdapterDescriptor> {
        const executable = this.resolveAdapterExecutable();

        this._statusBar.setConnecting(session.configuration['host'] as string);

        vscode.debug.onDidTerminateDebugSession(s => {
            if (s.id === session.id) {
                this._statusBar.setDisconnected();
            }
        });

        return new vscode.DebugAdapterExecutable(executable.command, executable.args, {
            // Adapter logs go to stderr; VS Code surfaces them in the Debug Console
            env: { ...process.env },
        });
    }

    private resolveAdapterExecutable(): { command: string; args: string[] } {
        const binDir = path.join(this._context.extensionPath, 'bin');
        const nativeWindows = path.join(binDir, 'pidbg-adapter.exe');
        const nativePosix = path.join(binDir, 'pidbg-adapter');
        const managedDll = path.join(binDir, 'pidbg-adapter.dll');

        if (process.platform === 'win32' && fs.existsSync(nativeWindows)) {
            return { command: nativeWindows, args: [] };
        }

        if (process.platform !== 'win32' && fs.existsSync(nativePosix)) {
            return { command: nativePosix, args: [] };
        }

        if (fs.existsSync(managedDll)) {
            return { command: 'dotnet', args: [managedDll] };
        }

        throw new Error(
            'PiDbg adapter not found. Expected one of: ' +
            `${nativeWindows}, ${nativePosix}, or ${managedDll}. ` +
            'Build/publish PiDbg.DebugAdapter into the extension bin folder first.'
        );
    }
}
