import java.io.File;

/**
 * OptiFine 安装器的无界面入口：OptiFine 官方安装器自带 optifine.Installer.doInstall(File)，
 * 但它的 main() 只会安装到 %APPDATA%\.minecraft（工作目录写死），所以需要这个转发类把目标目录传进去。
 * 用法：java -cp "<本类所在目录>;<OptiFine 安装器.jar>" OptiFineHeadless "<.minecraft 目录>"
 */
public class OptiFineHeadless {
    public static void main(String[] args) throws Exception {
        if (args.length < 1) {
            System.err.println("usage: OptiFineHeadless <minecraft-dir>");
            System.exit(2);
        }

        optifine.Installer.doInstall(new File(args[0]));
    }
}
