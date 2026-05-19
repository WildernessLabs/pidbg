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

        // pidbg-adapter.exe lives next to the extension in bin/
        const adapterPath = path.join(
            this._context.extensionPath, 'bin', 'pidbg-adapter.exe');

        this._statusBar.setConnecting(session.configuration['host'] as string);

        vscode.debug.onDidTerminateDebugSession(s => {
            if (s.id === session.id) {
                this._statusBar.setDisconnected();
            }
        });

        return new vscode.DebugAdapterExecutable(adapterPath, [], {
            // Adapter logs go to stderr; VS Code surfaces them in the Debug Console
            env: { ...process.env },
        });
    }
}
