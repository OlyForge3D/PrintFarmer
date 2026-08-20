import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { Input } from '../Input';

describe('Input', () => {
  it('should render text input', () => {
    render(<Input />);
    
    expect(screen.getByRole('textbox')).toBeInTheDocument();
  });

  it('should call onChange when typing', () => {
    const handleChange = vi.fn();
    render(<Input onChange={handleChange} />);
    
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'hello' } });
    expect(handleChange).toHaveBeenCalled();
  });

  it('should show entered value', () => {
    render(<Input value="test value" onChange={() => {}} />);
    
    expect(screen.getByRole('textbox')).toHaveValue('test value');
  });

  it('should be disabled when disabled prop is true', () => {
    render(<Input disabled />);
    
    expect(screen.getByRole('textbox')).toBeDisabled();
  });

  it('should apply invalid styling when invalid prop is true', () => {
    render(<Input invalid data-testid="invalid-input" />);
    
    expect(screen.getByTestId('invalid-input')).toBeInTheDocument();
  });

  it('should apply custom className', () => {
    render(<Input className="custom-input" />);
    
    expect(screen.getByRole('textbox')).toHaveClass('custom-input');
  });

  it('should pass through additional props', () => {
    render(<Input name="email" id="email-input" placeholder="Enter email" />);
    
    const input = screen.getByRole('textbox');
    expect(input).toHaveAttribute('name', 'email');
    expect(input).toHaveAttribute('id', 'email-input');
    expect(input).toHaveAttribute('placeholder', 'Enter email');
  });

  it('should support type="email"', () => {
    render(<Input type="email" placeholder="Email" />);
    
    expect(screen.getByPlaceholderText('Email')).toHaveAttribute('type', 'email');
  });

  it('should support type="password"', () => {
    render(<Input type="password" placeholder="Password" />);
    
    expect(screen.getByPlaceholderText('Password')).toHaveAttribute('type', 'password');
  });

  it('should support type="number"', () => {
    render(<Input type="number" placeholder="Amount" />);
    
    expect(screen.getByPlaceholderText('Amount')).toHaveAttribute('type', 'number');
  });

  it('should support required attribute', () => {
    render(<Input required />);
    
    expect(screen.getByRole('textbox')).toBeRequired();
  });

  it('should support maxLength attribute', () => {
    render(<Input maxLength={50} />);
    
    expect(screen.getByRole('textbox')).toHaveAttribute('maxLength', '50');
  });

  it('should support pattern attribute', () => {
    render(<Input pattern="[A-Za-z]{3}" />);
    
    expect(screen.getByRole('textbox')).toHaveAttribute('pattern', '[A-Za-z]{3}');
  });

  it('should blur a focused number input on wheel to avoid hijacking scroll', () => {
    render(<Input type="number" placeholder="Amount" />);

    const input = screen.getByPlaceholderText('Amount');
    input.focus();
    expect(input).toHaveFocus();

    fireEvent.wheel(input, { deltaY: 100 });

    expect(input).not.toHaveFocus();
  });

  it('should not blur non-number inputs on wheel', () => {
    render(<Input placeholder="Text" />);

    const input = screen.getByPlaceholderText('Text');
    input.focus();

    fireEvent.wheel(input, { deltaY: 100 });

    expect(input).toHaveFocus();
  });

  it('should still call a caller-provided onWheel handler for number inputs', () => {
    const handleWheel = vi.fn();
    render(<Input type="number" placeholder="Amount" onWheel={handleWheel} />);

    fireEvent.wheel(screen.getByPlaceholderText('Amount'), { deltaY: 100 });

    expect(handleWheel).toHaveBeenCalled();
  });

  it('should NOT cancel a wheel event over an unfocused number input', () => {
    // Regression guard for issue #1745: an earlier version of this fix called
    // preventDefault()/blur() on every wheel event over a number input,
    // regardless of focus. That cancelled the browser's whole scroll
    // gesture whenever the cursor happened to be over an *unfocused* number
    // input, making a scroll container (e.g. the Add Printer modal body)
    // unscrollable past it. Chromium's increment/refocus default action only
    // applies to a *focused* number input, so the fix must only intercept
    // (and therefore only cancel) the wheel event while the field is focused.
    render(<Input type="number" placeholder="Amount" />);

    const input = screen.getByPlaceholderText('Amount');
    expect(input).not.toHaveFocus();

    // fireEvent returns `false` when the event was cancelled via
    // preventDefault(), and `true` otherwise.
    const notCancelled = fireEvent.wheel(input, { deltaY: 100, cancelable: true });

    expect(notCancelled).toBe(true);
    expect(input).not.toHaveFocus();
  });

  it('should cancel a wheel event over a focused number input', () => {
    render(<Input type="number" placeholder="Amount" />);

    const input = screen.getByPlaceholderText('Amount');
    input.focus();
    expect(input).toHaveFocus();

    const notCancelled = fireEvent.wheel(input, { deltaY: 100, cancelable: true });

    expect(notCancelled).toBe(false);
    expect(input).not.toHaveFocus();
  });

  it('should attach the wheel listener as non-passive so preventDefault() is effective', () => {
    // Regression guard: React's onWheel prop is passive, which is exactly
    // why a real, non-passive DOM listener is required for this fix to work
    // in real browsers (fireEvent.wheel in jsdom doesn't reproduce passive-
    // listener semantics, so this must be asserted directly against the
    // addEventListener call).
    const addEventListenerSpy = vi.spyOn(HTMLInputElement.prototype, 'addEventListener');

    render(<Input type="number" placeholder="Amount" />);

    const wheelCall = addEventListenerSpy.mock.calls.find(([eventName]) => eventName === 'wheel');
    expect(wheelCall).toBeDefined();
    expect(wheelCall?.[2]).toEqual(expect.objectContaining({ passive: false }));

    addEventListenerSpy.mockRestore();
  });

  it('should not report spurious detach/attach on a caller-provided callback ref across rerenders', () => {
    // Regression guard: `setRefs` must be stable across renders. An
    // unmemoized callback ref would make React call the previous ref with
    // `null` and the new one with the same node on every rerender, even
    // though the underlying DOM node never changed — a false unmount/remount
    // signal that could break consumers doing setup/cleanup in their ref.
    const refCalls: Array<HTMLInputElement | null> = [];
    const callbackRef = (node: HTMLInputElement | null) => {
      refCalls.push(node);
    };

    const { rerender } = render(<Input ref={callbackRef} placeholder="Amount" />);
    expect(refCalls).toEqual([expect.any(HTMLInputElement)]);

    rerender(<Input ref={callbackRef} placeholder="Amount" />);
    rerender(<Input ref={callbackRef} placeholder="Amount" />);

    // Still just the single initial attach call — no spurious null/re-attach
    // pairs from rerenders that didn't replace the DOM node.
    expect(refCalls).toEqual([expect.any(HTMLInputElement)]);
  });

  it('should keep an object ref pointing at the same node across rerenders', () => {
    const objectRef = { current: null as HTMLInputElement | null };

    const { rerender } = render(<Input ref={objectRef} placeholder="Amount" />);
    const initialNode = objectRef.current;
    expect(initialNode).toBeInstanceOf(HTMLInputElement);

    rerender(<Input ref={objectRef} placeholder="Amount" />);

    expect(objectRef.current).toBe(initialNode);
  });
});
