
import * as spawn from 'cross-spawn';
import * as path from "path";
import * as fs from "fs";
import { SpawnSyncReturns } from 'node:child_process';

export class Example {
  readonly projectDir: string;
  readonly projectFile: string;

  constructor(projectName: string) {
    const projectDir = path.normalize(path.join(__dirname, `../examples/${projectName}/`))
      this.projectDir = projectDir,
      this.projectFile = path.join(projectDir, `${projectName}.proj`)
  }

  public clean() {
    const result = spawn.sync("git", ["clean", "-dfx", "--", this.projectDir], {
      cwd: this.projectDir,
      stdio: "pipe",
      encoding: "utf-8",
    });

    if(result.status != 0) {
      this.handleFailure("git", result);
    }
  }

  public build(runtimeSuffix: string, expectSuccess: boolean = true) {
    const result = spawn.sync("dotnet", ["build", `/p:RuntimeSuffix=${runtimeSuffix}`, "/bl", this.projectFile], {
      cwd: this.projectDir,
      stdio: "pipe",
      encoding: "utf-8",
    });

    if(expectSuccess && result.status != 0) {
      this.handleFailure("MSBuild", result);
    }

    return result;
  }

  public expectTemplate(relativeFilePath: string) {
    const filePath = path.join(this.projectDir, relativeFilePath);
    expect(fs.existsSync(filePath)).toBeTruthy();

    // we don't need to do full template validation
    // (the CLI tests already cover it)
    try {
      JSON.parse(fs.readFileSync(filePath, { encoding: "utf-8" }));
    } catch (error) {
      fail(error);
    }
  }

  private handleFailure(tool: string, result: SpawnSyncReturns<string>) {
    if (result.stderr.length > 0) {
      fail(result.stderr);
    }

    if (result.stdout.length > 0) {
      fail(result.stdout);
    }

    fail(`${tool} exit code ${result.status}`);
  }
}
