import * as path from 'path';
import * as vscode from 'vscode';

const DEFAULT_HOST = 'raspberrypi.local';
const DEFAULT_USERNAME = 'pi';

export class PiDbgConfigurationProvider implements vscode.DebugConfigurationProvider {

    constructor(private readonly context: vscode.ExtensionContext) { }

    async provideDebugConfigurations(
        folder: vscode.WorkspaceFolder | undefined,
        token?: vscode.CancellationToken
    ): Promise<vscode.DebugConfiguration[]> {
        if (!folder) {
            return [];
        }

        const projects = await this.findCsprojFiles(folder, token);
        return projects.map(uri => this.buildConfig(uri));
    }

    async resolveDebugConfiguration(
        folder: vscode.WorkspaceFolder | undefined,
        config: vscode.DebugConfiguration,
        token?: vscode.CancellationToken
    ): Promise<vscode.DebugConfiguration | undefined> {
        if (!config.type && !config.request) {
            const projects = folder ? await this.findCsprojFiles(folder, token) : [];
            if (projects.length === 0) {
                vscode.window.showErrorMessage('pidbg: no .csproj file found in this workspace.');
                return undefined;
            }
            config = this.buildConfig(projects[0]);
        }

        if (!config.projectPath) {
            const projects = folder ? await this.findCsprojFiles(folder, token) : [];
            if (projects.length === 1) {
                config.projectPath = projects[0].fsPath;
            } else {
                const picked = await vscode.window.showOpenDialog({
                    title: 'Select the .csproj to debug',
                    filters: { 'C# Project': ['csproj'] },
                    canSelectMany: false
                });
                if (!picked || picked.length === 0) {
                    vscode.window.showErrorMessage('pidbg: a projectPath is required to debug on Raspberry Pi.');
                    return undefined;
                }
                config.projectPath = picked[0].fsPath;
            }
        }

        if (!config.appName) {
            config.appName = path.basename(config.projectPath, '.csproj');
        }

        if (!config.host || config.host === DEFAULT_HOST) {
            const lastHost = this.context.globalState.get<string>('pidbg.lastHost');
            const host = await vscode.window.showInputBox({
                title: 'PiDbg: Raspberry Pi hostname or IP',
                value: lastHost ?? DEFAULT_HOST
            });
            if (!host) {
                return undefined;
            }
            config.host = host;
        }

        if (!config.username || config.username === DEFAULT_USERNAME) {
            const lastUsername = this.context.globalState.get<string>('pidbg.lastUsername');
            const username = await vscode.window.showInputBox({
                title: 'PiDbg: SSH username',
                value: lastUsername ?? DEFAULT_USERNAME
            });
            if (!username) {
                return undefined;
            }
            config.username = username;
        }

        await this.context.globalState.update('pidbg.lastHost', config.host);
        await this.context.globalState.update('pidbg.lastUsername', config.username);

        return config;
    }

    private async findCsprojFiles(
        folder: vscode.WorkspaceFolder,
        token?: vscode.CancellationToken
    ): Promise<vscode.Uri[]> {
        return vscode.workspace.findFiles(
            new vscode.RelativePattern(folder, '**/*.csproj'),
            new vscode.RelativePattern(folder, '**/{bin,obj}/**'),
            50,
            token
        );
    }

    private buildConfig(csprojUri: vscode.Uri): vscode.DebugConfiguration {
        const appName = path.basename(csprojUri.fsPath, '.csproj');
        return {
            type: 'pidbg',
            request: 'launch',
            name: `Debug ${appName} on Raspberry Pi`,
            host: DEFAULT_HOST,
            username: DEFAULT_USERNAME,
            privateKeyPath: '${userHome}/.ssh/pidbg_rsa',
            appName: appName,
            projectPath: csprojUri.fsPath,
            rootFolder: '~/meadow'
        };
    }
}
