import * as vscode from 'vscode';

export class PiDbgStatusBar implements vscode.Disposable {
    private readonly _item: vscode.StatusBarItem;

    constructor() {
        this._item = vscode.window.createStatusBarItem(
            vscode.StatusBarAlignment.Left, 100);
        this._item.command = 'pidbg.showOutput';
        this.setDisconnected();
    }

    setConnecting(host: string): void {
        this._item.text    = `$(loading~spin) PiDbg: ${host}`;
        this._item.tooltip = 'PiDbg — connecting…';
        this._item.show();
    }

    setConnected(host: string): void {
        this._item.text    = `$(debug-alt) PiDbg: ${host}`;
        this._item.tooltip = `PiDbg — connected to ${host}`;
        this._item.show();
    }

    setDisconnected(): void {
        this._item.text    = '$(debug-disconnect) PiDbg';
        this._item.tooltip = 'PiDbg — not connected';
        this._item.hide();
    }

    dispose(): void {
        this._item.dispose();
    }
}
