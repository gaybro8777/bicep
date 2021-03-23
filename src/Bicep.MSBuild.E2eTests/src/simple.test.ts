// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { Example } from "./example";

test("simple", () => {
  const example = new Example("simple");
  example.clean();

  const result = example.build();

  expect(result.stderr).toBe('');

  example.expectTemplate("bin/debug/net472/empty.json");
});
