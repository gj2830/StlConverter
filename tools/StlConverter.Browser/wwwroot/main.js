// Starts the .NET runtime in the page and runs the app (Program.Main), which draws into <div id="out">.
import { dotnet } from './_framework/dotnet.js'

const runtime = await dotnet
    .withApplicationArgumentsFromQuery()
    .create();

const config = runtime.getConfig();
await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);
