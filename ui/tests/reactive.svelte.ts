/** A value that a test changes, which the derived values that read it follow. */
export class Reactive<T> {
   value = $state.raw<T>() as T;

   constructor(value: T) {
      this.value = value;
   }
}
