// ...existing code...
-    // skeleton cards render no textual content; previous test used getAllByText(/./) which is brittle
-    const skeletons = screen.getAllByText(/./);
-    expect(skeletons.length).toBeGreaterThanOrEqual(3);
+    // skeleton cards expose an aria-label for loading state — assert using that instead of matching any text
+    const loadingCards = screen.getAllByLabelText(/Loading printer/i);
+    expect(loadingCards.length).toBeGreaterThanOrEqual(3);
// ...existing code...