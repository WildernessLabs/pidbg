import * as path from 'path';
import * as vscode from 'vscode';
import { PiDbgStatusBar } from './statusBar';

export class PiDbgDebugAdapterDescriptorFactory
    implements vscode.DebugAdapterDescriptorFactory {

    private readonly _context: vscode.ExtensionContext;
    private readonly _statusBar: PiDbgStatusBar;
    private readonly _output: vscode.OutputChannel;

    constructor(
        context: vscode.ExtensionContext,
        statusBar: PiDbgStatusBar,
        output: vscode.OutputChannel
    ) {
        this._context   = context;
        this._statusBar = statusBar;
        this._output    = output;
    }

    createDebugAdapterDescriptor(
        session: vscode.DebugSession,
        _executable: vscode.DebugAdapterExecutable | undefined
    ): vscode.ProviderResult<vscode.DebugAdapterDescriptor> {

        const adapterPath = path.join(
            this._context.extensionPath, 'bin', 'pidbg-adapter.exe');

        const host = session.configuration['host'] as string;
        this._statusBar.setConnecting(host);
        this._output.appendLine(`[pidbg] connecting to ${host}…`);
        this._output.appendLine(`[pidbg] adapter: ${adapterPath}`);
        this._output.show(true); // reveal without stealing focus

        const env = Object.fromEntries(
            Object.entries(process.env).filter((e): e is [string, string] => e[1] !== undefined)
        );

        return new vscode.DebugAdapterExecutable(adapterPath, [], { env });
    }
}
