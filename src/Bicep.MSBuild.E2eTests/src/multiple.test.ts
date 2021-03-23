// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import {Example} from "./example";

test("multiple", ()=>{
  const example = new Example("multiple");
  example.clean();

  const result = example.build("win-x64");

  expect(result.stderr).toBe('');

  example.expectTemplate("bin/debug/templates/empty.json");
  example.expectTemplate("bin/debug/templates/passthrough.json");
  example.expectTemplate("bin/debug/templates/special/special.arm");
})
