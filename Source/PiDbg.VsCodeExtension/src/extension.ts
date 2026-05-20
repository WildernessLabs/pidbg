import * as vscode from 'vscode';
import { PiDbgDebugAdapterDescriptorFactory } from './debugAdapterFactory';
import { PiDbgStatusBar } from './statusBar';

export function activate(context: vscode.ExtensionContext): void {
    const output = vscode.window.createOutputChannel('PiDbg');
    context.subscriptions.push(output);

    const statusBar = new PiDbgStatusBar();
    context.subscriptions.push(statusBar);

    const factory = new PiDbgDebugAdapterDescriptorFactory(context, statusBar, output);
    context.subscriptions.push(
        vscode.debug.registerDebugAdapterDescriptorFactory('pidbg', factory)
    );

    context.subscriptions.push(
        vscode.debug.onDidStartDebugSession(session => {
            if (session.type === 'pidbg') {
                statusBar.setConnected(session.configuration['host'] as string);
                output.appendLine(`[pidbg] session started: ${session.name}`);
            }
        })
    );

    context.subscriptions.push(
        vscode.debug.onDidTerminateDebugSession(session => {
            if (session.type === 'pidbg') {
                statusBar.setDisconnected();
                output.appendLine(`[pidbg] session terminated: ${session.name}`);
            }
        })
    );

    context.subscriptions.push(
        vscode.commands.registerCommand('pidbg.showOutput', () => output.show())
    );
}

export function deactivate(): void { /* nothing to clean up */ }
