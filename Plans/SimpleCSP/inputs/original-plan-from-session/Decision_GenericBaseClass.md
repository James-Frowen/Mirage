# Design Decision: Generic Base Class vs Interface/Component Architecture

## Overview
How do we expose the CSP functionality to the developer? We need a way for the system to manage history buffers and execute rollbacks, while the developer provides the actual inputs and state logic.

---

## Approach A: Generic Base Class (`PredictionBehaviour<TInput, TState>`)
The developer creates a class like `public class PlayerMovement : PredictionBehaviour<MyInput, MyState>` and overrides methods.

### Pros
- **Type Safety & Zero Allocation**: By defining `TInput` and `TState` as generic constraints (structs), the history buffers inside the base class (`RingBuffer<TInput>`) are strongly typed value arrays. There is zero boxing/unboxing allocation when reading or writing state.
- **Encapsulated Flow**: The base class entirely hides the complexity of RPCs, Tick counting, and the rollback `for` loop. The developer only sees `ProcessInput` and `ApplyState`.
- **Clear Contract**: It forces the developer to explicitly define what constitutes an "Input" and a "State", which naturally leads to better deterministic design.

### Cons
- **Single Inheritance Limit**: The developer cannot inherit from another class (e.g., they can't inherit from `HealthBehaviour` if they also want to be a `PredictionBehaviour`). 
- **Slightly Verbose**: Requires defining two structs for every predicted component.
- **Multiple Predictors**: If an object needs to predict movement *and* weapon firing, putting them in one giant `PredictionBehaviour` state gets messy, but adding two generic components might cause RPC conflicts if not routed carefully.

### Rating: 9 / 10
*(Highly performant and enforces good habits).*

---

## Approach B: Interface-based / Component Manager (`IPredictable`)
A master `PredictionManager` component sits on the object, and loops through all attached `IPredictable` components on that GameObject.

### Pros
- **Modular**: A developer can attach a `MovementPredictor` and a `WeaponPredictor` as separate small components.
- **No Inheritance Issues**: Components just implement `IPredictable` and inherit from whatever they want.

### Cons
- **Boxing/Serialization Overhead**: If the `PredictionManager` is generic, how does it store the history of *all* `IPredictable` components? It usually has to serialize the state to byte arrays (`NetworkWriter`) on every tick, or box them into `object` arrays. Both create garbage (GC spikes) or burn CPU cycles.
- **Complex Syncing**: Rolling back requires iterating through components in a specific deterministic order.

### Rating: 5 / 10
*(Great for flexibility, terrible for CPU/Memory performance in a hot loop).*

---

## Approach C: Byte Array State (The Old Mirage CSP Way)
State is written directly to a `NetworkWriter` buffer on every tick. 

### Pros
- **Highly Flexible**: Can serialize anything, including dynamically changing list sizes.

### Cons
- **Very Slow**: Writing to a byte stream and reading from it 60 times a second per object, and then doing it 60 times over during a single rollback frame, completely tanks CPU performance.

### Rating: 4 / 10
*(Too heavy for a modern, simple CSP).*

---

## Recommendation for Simple CSP
**Go with Approach A (Generic Base Class)**. 
For Unity C# networking, avoiding garbage allocation is paramount. The generic base class allows us to create zero-allocation ring buffers of structs. To solve the "multiple predictors" issue, the developer can simply compose a larger struct that contains both MovementState and WeaponState.
