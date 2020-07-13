//
//  TagInfoViewController.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 6/28/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit
import CryptoKit
import CoreNFC

class TagInfoViewController: UIViewController, NFCTagReaderSessionDelegate {
    @IBOutlet var amiiboArt: UIImageView!
    @IBOutlet var characterName: UILabel!
    @IBOutlet var seriesName: UILabel!
    @IBOutlet var typeName: UILabel!
    @IBOutlet var tagUid: UILabel!
    
    var tagReaderSession: NFCTagReaderSession?
    var isLoaded = false
    
    fileprivate var _amiiboData: TagDump?
    var amiiboData: TagDump? {
        get {
            return _amiiboData
        }
        set(value) {
            _amiiboData = value
            if isLoaded {
                displayInfo(value: value)
            }
        }
    }
    var dump: Data {
        return amiiboData!.data
    }
    
    var fileName: String {
        let formatter = DateFormatter()
        formatter.timeZone = TimeZone.current
        formatter.dateFormat = "yyyy-MM-dd HH-mm-ss"
        
        return "\(characterName.text!) \(formatter.string(from: Date()))"
    }
    
    func displayInfo(value: TagDump?) {
        guard let value = value else {
            return
        }
        
        tagUid.text = "0x\(value.uid.map { String(format: "%02hhx", $0) }.joined())"
        characterName.text = "0x\(value.headHex)\(value.tailHex)"
        typeName.text = "0x\(value.headHex.prefix(8).suffix(2))"
        seriesName.text = "0x\(value.headHex.prefix(3))"
        
        var imageFilename = "icon_\(value.headHex)-\(value.tailHex)"
        if let realId = AmiiboDatabase.fakeAmiibo["\(value.headHex)\(value.tailHex)"] {
            imageFilename = "icon_\(realId.prefix(8))-\(realId.suffix(8))"
        }
        
        if let imagePath = try? Bundle.main.path(forResource: imageFilename, ofType: "png", inDirectory: "images", forLocalization: nil),
            let image = UIImage(contentsOfFile: imagePath) {
            amiiboArt.image = image
        }
        
        let json = AmiiboDatabase.database
        if let name = amiiboData?.amiiboName {
            characterName.text = name
        }
        
        if let type = amiiboData?.typeName {
            typeName.text = type
        }
        
        if let series = amiiboData?.amiiboSeriesName {
            seriesName.text = series
        }
    }
    
    @IBAction func saveTagTap(_ sender: Any) {
        let contentURL = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent(fileName)
            .appendingPathExtension("bin")
        
        guard let _ = try? dump.write(to: contentURL) else {
            return
        }

        // set up activity view controller
        let imageToShare = [ contentURL ]
        let activityViewController = UIActivityViewController(activityItems: imageToShare, applicationActivities: nil)
        activityViewController.popoverPresentationController?.sourceView = self.view // so that iPads won't crash
        activityViewController.completionWithItemsHandler = { (activityType, completed:Bool, returnedItems:[Any]?, error: Error?) in
           try? FileManager.default.removeItem(at: contentURL)
        }

        // present the view controller
        self.present(activityViewController, animated: true)
    }
    
    @IBAction func writeTagTap(_ sender: Any) {
        tagReaderSession = NFCTagReaderSession(pollingOption: NFCTagReaderSession.PollingOption.iso14443, delegate: self)
        tagReaderSession?.alertMessage = "Hold blank NTAG215 tag to phone"
        tagReaderSession?.begin()
    }
    
    @IBAction func showQrTap(_ sender: Any) {
       let storyboard = UIStoryboard(name: "Main", bundle: nil)
        guard let vc = storyboard.instantiateViewController(withIdentifier: "QrView") as? QrViewController else {
            return
        }
        self.present(vc, animated: true)
        vc.qrData = NSData(data: dump).base64EncodedString(options: .endLineWithLineFeed)
        vc.fileName = self.fileName
    }
    
    func writeDump(to ntag215Tag: NTAG215Tag, appData: Bool = false) {
        guard
            let lockedSecret = KeyFiles.lockedSecret,
            let unfixedInfo = KeyFiles.unfixedInfo,
            let staticKey = TagKey(data: lockedSecret),
            let dataKey = TagKey(data: unfixedInfo) else {
            return
        }
        
        if appData {
            let dump = ntag215Tag.dump
            if "\(dump.headHex)\(dump.tailHex)" == "\(amiiboData!.headHex)\(amiiboData!.tailHex)" {
                ntag215Tag.patchAndWriteAppData(amiiboData!, staticKey: staticKey, dataKey: dataKey) {result in
                    switch result {
                    case .success:
                        self.tagReaderSession?.invalidate()
                        
                        let alert = UIAlertController(title: "Tag app data successfully written", message: nil, preferredStyle: .alert)
                        alert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                        DispatchQueue.main.async {
                            self.present(alert, animated: true, completion: nil)
                        }
                        
                    case .failure(let error):
                        self.tagReaderSession?.invalidate(errorMessage: error.localizedDescription)
                    }
                }
            } else {
                self.tagReaderSession?.invalidate(errorMessage: "Tag character doesn't match")
            }
        } else {
            ntag215Tag.patchAndWriteDump(amiiboData!, staticKey: staticKey, dataKey: dataKey) {result in
                switch result {
                case .success:
                    self.tagReaderSession?.invalidate()
                    
                    let alert = UIAlertController(title: "Tag successfully written", message: nil, preferredStyle: .alert)
                    alert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                    DispatchQueue.main.async {
                        self.present(alert, animated: true, completion: nil)
                    }
                    
                case .failure(let error):
                    self.tagReaderSession?.invalidate(errorMessage: error.localizedDescription)
                }
            }
        }
    }
    
    func handleConnectedTag(tag: NFCMiFareTag) {
        NTAG215Tag.initialize(tag: tag) { result in
            switch result {
            case .success(let ntag215Tag):
                if ntag215Tag.dump.isLocked {
                    if ntag215Tag.dump.isAmiibo {
                        self.writeDump(to: ntag215Tag, appData: true)
                    } else {
                        self.tagReaderSession?.invalidate(errorMessage: "Tag is not an amiibo")
                    }
                } else {
                    self.writeDump(to: ntag215Tag)
                }
            case .failure(let error):
                self.tagReaderSession?.invalidate(errorMessage: error.localizedDescription)
            }
        }
    }
    
    override func viewDidLoad() {
        super.viewDidLoad()
        isLoaded = true
        if let data = amiiboData {
            displayInfo(value: data)
        }
    }
    
    // MARK: NFCTagReaderSessionDelegate
    func tagReaderSessionDidBecomeActive(_ session: NFCTagReaderSession) {
        NSLog("tagReaderSessionDidBecomeActive")
    }
    
    func tagReaderSession(_ session: NFCTagReaderSession, didInvalidateWithError error: Error) {
        NSLog("NFCTagReaderSession, didInvalidateWithError \(error)")
    }
    
    func tagReaderSession(_ session: NFCTagReaderSession, didDetect tags: [NFCTag]) {
        guard case let NFCTag.miFare(tag) = tags.first! else {
            tagReaderSession?.invalidate(errorMessage: "Invalid tag type")
            return
        }
        session.connect(to: tags.first!) { (error: Error?) in
            if let error = error {
                session.invalidate(errorMessage: error.localizedDescription)
            } else {
                self.handleConnectedTag(tag: tag)
            }
        }
    }
}
