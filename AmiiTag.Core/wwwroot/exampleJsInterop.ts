// This is a JavaScript module that is loaded on demand. It can export any number of
// functions, and may import other JavaScript modules if required.

/**
 * 
 * @param message
 * @returns
 */
export function showPrompt(message: string): string {
  return prompt(message, 'Type anything here');
}
