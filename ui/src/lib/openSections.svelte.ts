/** Which sections of one stop's explanation the reviewer opened or closed, by section ID, kept outside the component so a remounted explanation keeps them. */
export class OpenSections {
   private readonly opened = $state<Record<string, boolean>>({});

   /** Returns whether the reviewer opened the section, or undefined when they did neither. */
   get(id: string): boolean | undefined {
      return this.opened[id];
   }

   set(id: string, open: boolean): void {
      this.opened[id] = open;
   }
}
