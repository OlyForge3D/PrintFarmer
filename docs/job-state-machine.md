# Job State Machine Documentation

## Overview

The Job State Machine library provides a formal lifecycle management system for jobs in PrintFarmer. It enforces valid state transitions and provides validation methods to ensure job integrity throughout their lifecycle.

## State Flow

The formal job lifecycle follows this pattern:

```
queued → dispatched → processing → (succeeded | failed | cancelled | dead-letter)
```

## States

### Non-Terminal States
- **Queued** (0): Job has been created and is waiting to be assigned
- **Dispatched** (1): Job has been assigned to a processor but not yet started  
- **Processing** (2): Job is actively being processed

### Terminal States
- **Succeeded** (3): Job completed successfully
- **Failed** (4): Job failed during processing
- **Cancelled** (5): Job was cancelled by user or system
- **DeadLetter** (6): Job failed in an unrecoverable way and cannot be retried

## Valid Transitions

| From State | Valid Next States |
|------------|------------------|
| Queued | Dispatched, Cancelled |
| Dispatched | Processing, Cancelled, Dead-Letter |
| Processing | Succeeded, Failed, Cancelled, Dead-Letter |
| Terminal States | *(none)* |

## Usage

### Basic Validation
```csharp
using Farm.Web.Shared;

// Check if a transition is valid
bool isValid = JobStateMachine.IsValidTransition(JobState.Queued, JobState.Dispatched);

// Validate and throw on invalid transition
JobStateMachine.ValidateTransition(JobState.Queued, JobState.Processing); // Throws InvalidJobStateTransitionException

// Perform transition with validation
JobState newState = JobStateMachine.Transition(JobState.Queued, JobState.Dispatched);
```

### Terminal State Checking
```csharp
// Check if a state is terminal (no further transitions possible)
bool isTerminal = JobStateMachine.IsTerminal(JobState.Succeeded); // true
bool canContinue = JobStateMachine.IsTerminal(JobState.Processing); // false

// Get all terminal states
var terminalStates = JobStateMachine.GetTerminalStates();
// Returns: [Succeeded, Failed, Cancelled, DeadLetter]
```

### Exploring Valid Transitions
```csharp
// Get all valid next states from current state
var validNext = JobStateMachine.GetValidNextStates(JobState.Queued);
// Returns: [Dispatched, Cancelled]

var noNext = JobStateMachine.GetValidNextStates(JobState.Succeeded);
// Returns: [] (empty - terminal state)
```

## Exception Handling

Invalid transitions throw `InvalidJobStateTransitionException`:

```csharp
try 
{
    JobStateMachine.Transition(JobState.Succeeded, JobState.Processing);
}
catch (InvalidJobStateTransitionException ex)
{
    Console.WriteLine($"Invalid transition: {ex.FromState} → {ex.ToState}");
    Console.WriteLine(ex.Message); // "Invalid transition from Succeeded to Processing"
}
```

## Integration Examples

### Typical Successful Workflow
```csharp
var jobState = JobState.Queued;
jobState = JobStateMachine.Transition(jobState, JobState.Dispatched);
jobState = JobStateMachine.Transition(jobState, JobState.Processing);  
jobState = JobStateMachine.Transition(jobState, JobState.Succeeded);
// Job completed successfully
```

### Early Cancellation
```csharp
var jobState = JobState.Queued;
// User cancels before processing starts
jobState = JobStateMachine.Transition(jobState, JobState.Cancelled);
// Job terminated early
```

### Error Handling
```csharp
var jobState = JobState.Processing;
try 
{
    // Processing logic here
    jobState = JobStateMachine.Transition(jobState, JobState.Succeeded);
}
catch (Exception ex)
{
    // Handle processing error
    jobState = JobStateMachine.Transition(jobState, JobState.Failed);
}
```

## Benefits

1. **Type Safety**: Enum-based states prevent invalid string values
2. **Validation**: Automatic validation prevents invalid transitions  
3. **Documentation**: Clear state flow helps developers understand job lifecycle
4. **Testing**: Comprehensive test coverage ensures reliability
5. **Extensibility**: Easy to add new states or modify transition rules

## State Diagram

See `docs/images/job-states.png` for a visual representation of the state machine.