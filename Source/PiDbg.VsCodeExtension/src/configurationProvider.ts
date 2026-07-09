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

        if (!config.host) {
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

        if (!config.username) {
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
        await this.persistToLaunchJson(folder, config);

        return config;
    }

    private async persistToLaunchJson(
        folder: vscode.WorkspaceFolder | undefined,
        config: vscode.DebugConfiguration
    ): Promise<void> {
        if (!folder) {
            return;
        }

        const launchSection = vscode.workspace.getConfiguration('launch', folder.uri);
        const configurations = launchSection.get<vscode.DebugConfiguration[]>('configurations') ?? [];

        const index = configurations.findIndex(c =>
            c.type === 'pidbg' && (c.projectPath === config.projectPath || c.name === config.name)
        );
        if (index === -1) {
            // No launch.json entry yet (e.g. F5 was pressed directly without first
            // running "Add Configuration") - create one instead of silently skipping.
            configurations.push(config);
        } else {
            configurations[index] = { ...configurations[index], host: config.host, username: config.username };
        }

        await launchSection.update('configurations', configurations, vscode.ConfigurationTarget.WorkspaceFolder);
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
            host: '',
            username: '',
            privateKeyPath: '${userHome}/.ssh/pidbg_rsa',
            appName: appName,
            projectPath: csprojUri.fsPath,
            rootFolder: '~/meadow'
        };
    }
}
