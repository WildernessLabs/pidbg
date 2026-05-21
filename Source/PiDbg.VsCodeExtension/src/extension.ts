import * as vscode from 'vscode';
import { PiDbgDebugAdapterDescriptorFactory } from './debugAdapterFactory';
import { PiDbgStatusBar } from './statusBar';

export function activate(context: vscode.ExtensionContext): void {
    const statusBar = new PiDbgStatusBar();
    context.subscriptions.push(statusBar);

    const factory = new PiDbgDebugAdapterDescriptorFactory(context, statusBar);
    context.subscriptions.push(
        vscode.debug.registerDebugAdapterDescriptorFactory('pidbg', factory)
    );

    context.subscriptions.push(
        vscode.commands.registerCommand('pidbg.connectToDevice', () => {
            vscode.window.showInformationMessage('PiDbg: Start a "pidbg" debug session to connect to a device.');
        })
    );

    context.subscriptions.push(
        vscode.commands.registerCommand('pidbg.showOutput', () => {
            vscode.debug.activeDebugConsole.appendLine('[PiDbg] Use the Debug Console for output.');
        })
    );
}

export function deactivate(): void { /* nothing to clean up */ }
