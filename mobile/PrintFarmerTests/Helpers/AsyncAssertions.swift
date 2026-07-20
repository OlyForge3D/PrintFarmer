import XCTest

/// XCTest's `XCTAssertTrue` takes an `@autoclosure () throws -> Bool` argument.
/// Swift disallows `async` calls inside a non-async autoclosure, so
/// `XCTAssertTrue(await someAsync(), "msg")` does not compile.
///
/// This wrapper accepts a plain `Bool`, letting callers `await` at the call
/// site: `XCTAssertAwait(await someAsync(), "msg")`. The message can be a
/// static string; file/line default to the call site so failures point at the
/// original assertion.
func XCTAssertAwait(
    _ value: Bool,
    _ message: @autoclosure () -> String = "",
    file: StaticString = #file,
    line: UInt = #line
) {
    XCTAssertTrue(value, message(), file: file, line: line)
}
