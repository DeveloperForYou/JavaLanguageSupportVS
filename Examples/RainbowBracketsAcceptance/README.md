# Rainbow Brackets Acceptance (Java 17+)

Aprire `RainbowBracketsDemo.java` nell'istanza sperimentale Visual Studio 2026.
Verificare:
1. Le parentesi tonde, quadre e graffe sono colorate per annidamento misto.
2. La parentesi di apertura e quella di chiusura della stessa coppia hanno lo stesso colore.
3. Il colore ricomincia dal livello 1 dopo il livello 6.
4. Le parentesi dentro commenti, stringhe, char e text block NON sono colorate dal componente.
5. Le parentesi senza coppia (durante la digitazione) usano il colore base `Java: parentesi tonde, quadre e graffe`.
6. Personalizzare i livelli da Strumenti > Opzioni > Ambiente > Tipi di carattere e colori > Editor di testo.
7. Verificare il comportamento in tema scuro e tema chiaro.
8. Controllare che Java Semantic Highlighting, CodeLens e Inlay Hints restino operativi.

Compilazione senza build tool:
`javac -d out src/rainbow/RainbowBracketsDemo.java`
`java -cp out rainbow.RainbowBracketsDemo`

La compilazione Java NON verifica la compilazione della VSIX, né i colori dell'editor.
